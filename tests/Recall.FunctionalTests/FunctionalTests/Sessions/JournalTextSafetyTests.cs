namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Domain.Corrections.Dtos;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.UserSettings.Dtos;
using Shouldly;
using TestUtilities;

public class JournalTextSafetyTests : TestBase
{
    [Test]
    [Arguments("javascript:alert(1)")]
    [Arguments("JaVa\tScRiPt:alert(1)")]
    [Arguments(" data:text/html,x")]
    [Arguments("vbscript:x")]
    public async Task a_draft_with_a_link_that_no_page_may_open_is_refused_and_changes_nothing(string address)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var document = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"click","marks":[{"type":"link","attrs":{"href":ADDRESS}}]}]}]}""".Replace("ADDRESS", System.Text.Json.JsonSerializer.Serialize(address));

        // Act
        var raw = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Draft(session.Id), new { text = document, baseRevision = 0, savedAt = JournalScenario.Morning });
        var cleaned = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Cleaned(session.Id), new { text = document, baseRevision = 0, savedAt = JournalScenario.Morning });

        // Assert
        raw.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        cleaned.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var stored = await ExecuteDbContextAsync(db => db.Sessions.IgnoreQueryFilters().SingleAsync(s => s.Id == session.Id));
        stored.Raw.Text.ShouldNotContain("click");
        stored.Cleaned.Text.ShouldNotContain("click");
    }

    [Test]
    public async Task a_draft_with_a_web_link_saves()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        const string document = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"site","marks":[{"type":"link","attrs":{"href":"https://example.com/a:b"}}]},{"type":"text","text":" and "},{"type":"text","text":"note","marks":[{"type":"link","attrs":{"href":"/sessions/x:1"}}]}]}]}""";

        // Act
        var cleaned = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Cleaned(session.Id), new { text = document, baseRevision = 0, savedAt = JournalScenario.Morning });

        // Assert
        cleaned.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    [Property("Rule", "SES-19")]
    public async Task no_journal_text_reaches_a_log_a_span_or_an_audit_value_through_a_write_route()
    {
        // Arrange
        var marker = $"PRIVATEWORDS{Guid.NewGuid():N}";
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var firstSpan = AllExportedSpans().Count;
        var session = await FactoryClient.AddSessionAsync();

        // Act
        var responses = new List<HttpResponseMessage>
        {
            await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Draft(session.Id), new { text = $"raw {marker}", baseRevision = 0, savedAt = JournalScenario.Morning }),
            await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Draft(session.Id), new { text = $"lost {marker}", baseRevision = 0, savedAt = JournalScenario.Morning.AddMinutes(-5) }),
            await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Cleaned(session.Id), new { text = $"cleaned {marker}", baseRevision = 0, savedAt = JournalScenario.Morning }),
            await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { activity = marker, moods = new[] { marker }, userTopics = new[] { $"topic {marker}" }, metadataVersion = 0 }),
        };
        var person = await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label = $"Sam {marker}" });
        var personId = (await person.Content.ReadFromJsonAsync<PersonDto>())!.Id;
        responses.Add(person);
        responses.Add(await FactoryClient.PutAsJsonAsync(ApiRoutes.People.GetRecord(personId), new { label = $"Samuel {marker}" }));
        var correction = await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, new { canonicalTerm = $"term {marker}", mishearings = new[] { $"heard {marker}" } });
        var added = (await correction.Content.ReadFromJsonAsync<CorrectionDto>())!;
        responses.Add(correction);
        responses.Add(await FactoryClient.PutAsJsonAsync(ApiRoutes.Corrections.GetRecord(added.Id), new { canonicalTerm = $"changed {marker}", mishearings = new[] { $"misheard {marker}" }, version = added.Version }));
        responses.Add(await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new UserSettingChangeDto { TimeZone = "Europe/Tallinn", LocationCaptureEnabled = true, RequirePeopleProposalApproval = true, Version = 0 }));
        responses.Add(await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Draft(session.Id), new { text = new string('x', 1_000_001) + marker, baseRevision = 1, savedAt = JournalScenario.Morning }));
        responses.Add(await FactoryClient.DeleteAsync(ApiRoutes.Corrections.GetRecord(added.Id)));
        responses.Add(await FactoryClient.DeleteAsync(ApiRoutes.Sessions.GetRecord(session.Id)));

        // Assert
        responses[..^3].Select(r => r.StatusCode).Where(code => code is not (HttpStatusCode.OK or HttpStatusCode.Created)).ShouldBeEmpty();
        responses[^3].StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await responses[^3].Content.ReadAsStringAsync()).ShouldNotContain(marker);
        responses[^2].StatusCode.ShouldBe(HttpStatusCode.NoContent);
        responses[^1].StatusCode.ShouldBe(HttpStatusCode.NoContent);

        TestingWebApplicationFactory.Instance.CapturedLogs.Holding(marker).ShouldBeEmpty();

        var spans = AllExportedSpans().Skip(firstSpan).ToList();
        spans.ShouldNotBeEmpty();
        var spanText = spans.SelectMany(s => s.TagObjects.Select(t => $"{t.Key}={t.Value}")
            .Concat(s.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}").Append(e.Name)))
            .Append(s.DisplayName));
        spanText.Where(text => text.Contains(marker, StringComparison.Ordinal)).ShouldBeEmpty();

        var audit = await ExecuteDbContextAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .Where(e => e.RootId == session.Id || e.RootId == personId || e.RootId == added.Id)
            .Select(e => e.Data)
            .ToListAsync());
        audit.ShouldNotBeEmpty();
        audit.Where(data => data.Contains(marker, StringComparison.Ordinal)).ShouldBeEmpty();
    }
}
