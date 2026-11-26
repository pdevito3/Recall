namespace Recall.ContractTests;

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Recall.FunctionalTests;
using Recall.FunctionalTests.TestUtilities;
using Recall.Server.Databases;
using Recall.Server.Domain.Corrections.Dtos;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sync.Dtos;
using Recall.SharedTestHelpers.EntityGraphs;
using Shouldly;

public class JournalFixtureTests
{
    private static readonly DateTimeOffset Morning = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] Mishearings = ["tall in"];
    private static readonly string[] Moods = ["calm"];
    private static readonly string[] Topics = ["Food"];

    [Test]
    public async Task the_journal_routes_answer_as_the_saved_fixtures_say()
    {
        // Arrange
        var factory = TestingWebApplicationFactory.Instance;
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUser(out var member, u => u.Roles = [])
            .Under(tenant);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AddRange(tenant, member);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        client.SignInAsAppUser(member.Identifier);
        var fixtures = new ContractFixtures(FixtureDirectory());

        // Act and assert
        var person = await client.PostAsJsonAsync(ApiRoutes.People.Root, new { label = "Sam", fullName = "Sam Lee", notes = "From the climbing gym" });
        person.StatusCode.ShouldBe(HttpStatusCode.Created);
        await fixtures.CheckAsync("person", person);
        var personId = (await person.Content.ReadFromJsonAsync<PersonDto>())!.Id;

        var correction = await client.PostAsJsonAsync(ApiRoutes.Corrections.Root, new { canonicalTerm = "Tallinn", mishearings = Mishearings, hardReplace = true });
        correction.StatusCode.ShouldBe(HttpStatusCode.Created);
        await fixtures.CheckAsync("correction", correction);
        var correctionId = (await correction.Content.ReadFromJsonAsync<CorrectionDto>())!.Id;

        var settings = await client.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new
        {
            timeZoneMode = "Fixed",
            timeZone = "Europe/Tallinn",
            locationCaptureEnabled = true,
            requirePeopleProposalApproval = true,
            version = 0,
        });
        settings.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixtures.CheckAsync("user-settings", settings);

        var staleSettings = await client.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new
        {
            timeZoneMode = "Fixed",
            timeZone = "Europe/Paris",
            locationCaptureEnabled = false,
            requirePeopleProposalApproval = true,
            version = 0,
        });
        staleSettings.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await fixtures.CheckAsync("error-stale-settings", staleSettings);

        var create = new { clientSessionId = "client-session-1", startedAt = Morning, latitude = 59.437, longitude = 24.7536 };
        var created = await client.PostAsJsonAsync(ApiRoutes.Sessions.Root, create);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        await fixtures.CheckAsync("session-created", created);
        var sessionId = (await created.Content.ReadFromJsonAsync<SessionDto>())!.Id;

        var replay = await client.PostAsJsonAsync(ApiRoutes.Sessions.Root, create);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixtures.CheckAsync("session-replayed", replay);

        var words = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Lunch with \"},"
            + "{\"type\":\"mention\",\"attrs\":{\"personId\":\"" + personId + "\",\"label\":\"Sam\"}}]}]}";
        var won = await client.PutAsJsonAsync(ApiRoutes.Sessions.Draft(sessionId), new { text = words, baseRevision = 0, savedAt = Morning.AddMinutes(5) });
        won.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixtures.CheckAsync("draft-save-won", won);

        var lost = await client.PutAsJsonAsync(ApiRoutes.Sessions.Draft(sessionId), new { text = "Older words", baseRevision = 0, savedAt = Morning.AddMinutes(1) });
        lost.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixtures.CheckAsync("draft-save-lost", lost);

        var cleaned = await client.PutAsJsonAsync(ApiRoutes.Sessions.Cleaned(sessionId), new { text = "Lunch with Sam.", baseRevision = 0, savedAt = Morning.AddMinutes(6) });
        cleaned.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixtures.CheckAsync("cleaned-save", cleaned);

        var metadata = await client.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(sessionId), new { activity = "walking", moods = Moods, userTopics = Topics, metadataVersion = 0 });
        metadata.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixtures.CheckAsync("session-metadata", metadata);

        var staleMetadata = await client.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(sessionId), new { activity = "walking", moods = Moods, userTopics = Array.Empty<string>(), metadataVersion = 0 });
        staleMetadata.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await fixtures.CheckAsync("error-stale-metadata", staleMetadata);

        await fixtures.CheckAsync("session", await GetOkAsync(client, ApiRoutes.Sessions.GetRecord(sessionId)));
        await fixtures.CheckAsync("raw-revision-page", await GetOkAsync(client, ApiRoutes.Sessions.RawRevisions(sessionId)));
        await fixtures.CheckAsync("raw-revision", await GetOkAsync(client, ApiRoutes.Sessions.RawRevision(sessionId, 1)));
        await fixtures.CheckAsync("cleaned-revision-page", await GetOkAsync(client, ApiRoutes.Sessions.CleanedRevisions(sessionId)));
        await fixtures.CheckAsync("cleaned-revision", await GetOkAsync(client, ApiRoutes.Sessions.CleanedRevision(sessionId, 1)));
        await fixtures.CheckAsync("people", await GetOkAsync(client, ApiRoutes.People.Root));
        await fixtures.CheckAsync("corrections", await GetOkAsync(client, ApiRoutes.Corrections.Root));

        var second = await client.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = "client-session-2", startedAt = Morning.AddHours(1) });
        second.StatusCode.ShouldBe(HttpStatusCode.Created);
        await fixtures.CheckAsync("session-page-with-next-cursor", await GetOkAsync(client, $"{ApiRoutes.Sessions.Root}?pageSize=1"));
        await fixtures.CheckAsync("session-page", await GetOkAsync(client, ApiRoutes.Sessions.Root));

        var firstPull = await GetOkAsync(client, ApiRoutes.Sync.Changes);
        await fixtures.CheckAsync("sync-changes", firstPull);
        var cursor = (await firstPull.Content.ReadFromJsonAsync<SyncChangePageDto>())!.Cursor;

        var deleted = await client.DeleteAsync(ApiRoutes.Sessions.GetRecord(sessionId));
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await fixtures.CheckAsync("sync-changes-with-tombstone", await GetOkAsync(client, $"{ApiRoutes.Sync.Changes}?cursor={cursor}"));

        var gone = await client.GetAsync(ApiRoutes.Sessions.GetRecord(sessionId));
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await fixtures.CheckAsync("error-not-found", gone);

        var replayAfterDelete = await client.PostAsJsonAsync(ApiRoutes.Sessions.Root, create);
        replayAfterDelete.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await fixtures.CheckAsync("error-replay-after-delete", replayAfterDelete);

        var ahead = await client.GetAsync($"{ApiRoutes.Sync.Changes}?cursor={long.MaxValue}");
        ahead.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await fixtures.CheckAsync("error-cursor-ahead", ahead);

        var third = await client.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = "client-session-3", startedAt = Morning.AddHours(2) });
        third.StatusCode.ShouldBe(HttpStatusCode.Created);
        var thirdRoute = ApiRoutes.Sessions.Draft((await third.Content.ReadFromJsonAsync<SessionDto>())!.Id);
        (await client.PutAsJsonAsync(thirdRoute, new { text = "Words of the first save", baseRevision = 0, savedAt = Morning.AddHours(2) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PutAsJsonAsync(thirdRoute, new { text = "Words of the second save", baseRevision = 1, savedAt = Morning.AddHours(3) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var kept = await client.PutAsJsonAsync(thirdRoute, new { text = "Words of the first save", baseRevision = 0, savedAt = Morning.AddHours(2) });
        kept.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixtures.CheckAsync("draft-save-kept", kept);

        (await client.DeleteAsync(ApiRoutes.Corrections.GetRecord(correctionId))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<HttpResponseMessage> GetOkAsync(HttpClient client, string route)
    {
        var response = await client.GetAsync(route);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response;
    }

    private static string FixtureDirectory([CallerFilePath] string source = "")
    {
        var directory = Path.Combine(Path.GetDirectoryName(source)!, "fixtures");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
