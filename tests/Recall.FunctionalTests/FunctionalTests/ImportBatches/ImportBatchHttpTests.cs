namespace Recall.FunctionalTests.FunctionalTests.ImportBatches;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.FunctionalTests.Auth;
using Recall.FunctionalTests.FunctionalTests.Sessions;
using Recall.Server.Domain.ImportBatches.Dtos;
using Recall.Server.Domain.ImportBatches.Features;
using Recall.Server.Domain.Users;
using Shouldly;
using TestUtilities;

public class ImportBatchHttpTests : TestBase
{
    private const string Day = "2024-03-01";

    [Test]
    [Property("Rule", "IMP-25")]
    public async Task no_word_of_an_entry_and_no_name_of_a_batch_reaches_a_log_a_span_or_an_audit_value()
    {
        // Arrange
        var marker = $"PRIVATEWORDS{Guid.NewGuid():N}";
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsWebSession(member.Identifier);
        var firstSpan = AllExportedSpans().Count;
        var opened = await FactoryClient.PostAsJsonAsync(ApiRoutes.ImportBatches.Root, new { name = $"Diary {marker}", timeZone = "UTC" });
        var batch = (await opened.Content.ReadFromJsonAsync<ImportBatchDto>()).ShouldNotBeNull();

        // Act
        var responses = new List<HttpResponseMessage>
        {
            opened,
            await FactoryClient.PutAsJsonAsync(ApiRoutes.ImportBatches.Entries(batch.Id), new
            {
                entries = new object[]
                {
                    new { clientSessionId = "kept", date = Day, position = 0, raw = $"raw {marker}", moods = new[] { marker }, topics = new[] { $"topic {marker}" } },
                    new { clientSessionId = "edited", date = Day, position = 1, raw = $"other {marker}" },
                    new { clientSessionId = "invalid", date = $"day {marker}", position = 2, raw = $"bad {marker}" },
                },
            }),
            await FactoryClient.GetAsync(ApiRoutes.ImportBatches.Entries(batch.Id)),
            await FactoryClient.PutAsJsonAsync(ApiRoutes.ImportBatches.ReviewEntry(batch.Id), new { clientSessionId = "edited", rejected = false, words = $"edit {marker}", time = "20:00" }),
            await FactoryClient.PutAsJsonAsync(ApiRoutes.ImportBatches.ReviewDay(batch.Id), new { date = "2024-03-02", rejected = true }),
            await FactoryClient.PostAsJsonAsync(ApiRoutes.ImportBatches.RemoveEntries(batch.Id), new { clientSessionIds = new[] { marker } }),
            await FactoryClient.GetAsync(ApiRoutes.ImportBatches.Root),
        };
        var version = (await FactoryClient.GetFromJsonAsync<ImportEntryPageDto>(ApiRoutes.ImportBatches.Entries(batch.Id)))!.Version;
        responses.Add(await FactoryClient.PostAsJsonAsync(ApiRoutes.ImportBatches.Accept(batch.Id), new { version }));

        // Assert
        responses.Select(r => (r.RequestMessage!.RequestUri!.AbsolutePath, r.StatusCode))
            .Where(r => r.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created)).ShouldBeEmpty();
        var accepted = (await responses[^1].Content.ReadFromJsonAsync<ImportBatchDto>()).ShouldNotBeNull();
        accepted.Result!.SessionsMade.ShouldBe(2);

        TestingWebApplicationFactory.Instance.CapturedLogs.Holding(marker).ShouldBeEmpty();
        var spans = AllExportedSpans().Skip(firstSpan).ToList();
        spans.ShouldNotBeEmpty();
        var spanText = spans.SelectMany(s => s.TagObjects.Select(t => $"{t.Key}={t.Value}")
            .Concat(s.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}").Append(e.Name)))
            .Append(s.DisplayName));
        spanText.Where(text => text.Contains(marker, StringComparison.Ordinal) || text.Contains("2024-03-02", StringComparison.Ordinal)).ShouldBeEmpty();
        var sessionIds = await ExecuteDbContextAsync(db => db.Sessions.IgnoreQueryFilters()
            .Where(s => s.ImportBatchId == batch.Id).Select(s => s.Id).ToListAsync());
        var audit = await ExecuteDbContextAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .Where(e => e.RootId == batch.Id || sessionIds.Contains(e.RootId))
            .Select(e => e.Data)
            .ToListAsync());
        audit.ShouldNotBeEmpty();
        audit.Where(data => data.Contains(marker, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "IMP-29")]
    public async Task a_session_read_names_the_batch_of_its_import_and_the_sync_feed_does_not()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsWebSession(member.Identifier);
        var device = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = "device", startedAt = DateTimeOffset.UtcNow.AddHours(-1) });
        var deviceId = (await device.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var opened = await FactoryClient.PostAsJsonAsync(ApiRoutes.ImportBatches.Root, new { name = "Diary", timeZone = "UTC" });
        var batch = (await opened.Content.ReadFromJsonAsync<ImportBatchDto>()).ShouldNotBeNull();
        var put = await FactoryClient.PutAsJsonAsync(ApiRoutes.ImportBatches.Entries(batch.Id), new
        {
            entries = new[] { new { clientSessionId = "imported", date = Day, position = 0, raw = "words" } },
        });
        var version = (await put.Content.ReadFromJsonAsync<ImportPutResultDto>())!.Version;
        (await FactoryClient.PostAsJsonAsync(ApiRoutes.ImportBatches.Accept(batch.Id), new { version })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var timeline = await FactoryClient.GetFromJsonAsync<JsonElement>(ApiRoutes.Sessions.Root);
        var items = timeline.GetProperty("items").EnumerateArray().ToList();
        var importedId = items.Single(item => item.GetProperty("clientSessionId").GetString() == "imported").GetProperty("id").GetString()!;

        // Act
        var imported = await FactoryClient.GetFromJsonAsync<JsonElement>(ApiRoutes.Sessions.GetRecord(importedId));
        var fromDevice = await FactoryClient.GetFromJsonAsync<JsonElement>(ApiRoutes.Sessions.GetRecord(deviceId));
        var feed = await FactoryClient.GetFromJsonAsync<JsonElement>(ApiRoutes.Sync.Changes);

        // Assert
        items.Select(item => (item.GetProperty("id").GetString(), item.GetProperty("importBatchId").GetString()))
            .ShouldBe([(importedId, batch.Id), (deviceId, null)], ignoreOrder: true);
        imported.GetProperty("importBatchId").GetString().ShouldBe(batch.Id);
        fromDevice.GetProperty("importBatchId").ValueKind.ShouldBe(JsonValueKind.Null);
        var sessions = feed.GetProperty("sessions").EnumerateArray().ToList();
        sessions.Select(session => session.GetProperty("id").GetString()).ShouldBe([deviceId, importedId], ignoreOrder: true);
        sessions.Where(session => session.TryGetProperty("importBatchId", out _)).ShouldBeEmpty();
    }

    [Test]
    [NotInParallel(nameof(AuthHosts.KeepAlive))]
    [Property("Rule", "IMP-28")]
    public async Task a_put_whose_body_is_more_than_16_mb_gets_413_on_kestrel_and_stores_nothing()
    {
        // Arrange
        var host = AuthHosts.KeepAlive;
        var (_, member) = await PatPeople.AddTenantAsync(host);
        var token = await PatPeople.StartTokenAsync(host, member, new TokenGrant([], JournalAccess.ReadWrite));
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        // Kestrel refuses the body before it reads it, so a client that does not wait for 100 Continue sees a broken pipe
        client.DefaultRequestHeaders.ExpectContinue = true;
        var opened = await client.PostAsJsonAsync(ApiRoutes.ImportBatches.Root, new { name = "Large", timeZone = "UTC" });
        var batch = (await opened.Content.ReadFromJsonAsync<ImportBatchDto>()).ShouldNotBeNull();
        var raw = new string('a', 900_000);
        var entries = Enumerable.Range(0, 19).Select(i => new { clientSessionId = $"large-{i}", date = Day, position = i, raw });
        using var body = new StringContent(JsonSerializer.Serialize(new { entries }), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PutAsync(ApiRoutes.ImportBatches.Entries(batch.Id), body);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>()).ShouldNotBeNull();
        problem.Detail.ShouldBe(PutImportEntriesEndpoint.TooLargeMessage);
        (await client.GetFromJsonAsync<ImportEntryPageDto>(ApiRoutes.ImportBatches.Entries(batch.Id)))!.Entries.ShouldBeEmpty();
    }
}
