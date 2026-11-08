namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Dtos;
using Shouldly;
using TestUtilities;

public class DraftChangeIdTests : TestBase
{
    [Test]
    [Property("Rule", "SES-46")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task the_revision_of_a_save_keeps_its_change_id_and_its_parent_and_the_revision_answers_give_them_back(bool raw)
    {
        // Arrange
        var session = await AddSessionOfNewPersonAsync();
        var route = RouteOf(session, raw);
        var now = DateTimeOffset.UtcNow;
        var won = await SaveAsync(route, new { text = "first", baseRevision = 0, savedAt = now, changeId = "change-1" });

        // Act
        var lost = await SaveAsync(route, new { text = "second", baseRevision = 0, savedAt = now.AddSeconds(1), changeId = "change-2", parentChangeId = "change-1" });
        var revision = await FactoryClient.GetFromJsonAsync<RevisionDto>($"{RevisionsOf(session, raw)}/{lost.LatestRevision}");
        var page = await FactoryClient.GetFromJsonAsync<RevisionPageDto>(RevisionsOf(session, raw));

        // Assert
        won.Outcome.ShouldBe("Won");
        lost.Outcome.ShouldBe("Lost");
        revision!.ChangeId.ShouldBe("change-2");
        revision.ParentChangeId.ShouldBe("change-1");
        page!.Items.Select(item => (item.Number, item.WasDraft, item.ChangeId, item.ParentChangeId)).ShouldBe([
            (2, false, "change-2", "change-1"),
            (1, true, "change-1", null),
        ]);
    }

    [Test]
    [Property("Rule", "SES-46")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task a_save_with_no_ids_wins_and_loses_as_before_and_its_revision_has_no_ids(bool raw)
    {
        // Arrange
        var session = await AddSessionOfNewPersonAsync();
        var route = RouteOf(session, raw);
        var now = DateTimeOffset.UtcNow;

        // Act
        var won = await SaveAsync(route, new { text = "first", baseRevision = 0, savedAt = now });
        var lost = await SaveAsync(route, new { text = "second", baseRevision = 0, savedAt = now.AddSeconds(1) });
        var replay = await SaveAsync(route, new { text = "first", baseRevision = 0, savedAt = now });
        var page = await FactoryClient.GetFromJsonAsync<RevisionPageDto>(RevisionsOf(session, raw));

        // Assert
        (won.Outcome, lost.Outcome, replay.Outcome).ShouldBe(("Won", "Lost", "Kept"));
        page!.Items.ShouldAllBe(item => item.ChangeId == null && item.ParentChangeId == null);
    }

    [Test]
    [Property("Rule", "SES-46")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task an_id_decides_no_save_so_a_save_from_an_old_base_loses_also_when_it_grew_from_the_draft(bool raw)
    {
        // Arrange
        var session = await AddSessionOfNewPersonAsync();
        var route = RouteOf(session, raw);
        var now = DateTimeOffset.UtcNow;
        var start = await SaveAsync(route, new { text = "start", baseRevision = 0, savedAt = now, changeId = "start" });
        await SaveAsync(route, new { text = "chunk", baseRevision = start.DraftRevision, savedAt = now.AddSeconds(1) });

        // Act
        var save = await SaveAsync(route, new { text = "start and more", baseRevision = start.DraftRevision, savedAt = now.AddSeconds(2), changeId = "more", parentChangeId = "start" });
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        save.Outcome.ShouldBe("Lost");
        (raw ? read!.RawText : read!.CleanedText).ShouldBe("chunk");
    }

    [Test]
    [Property("Rule", "SES-46")]
    [Arguments(true, "changeId", 1)]
    [Arguments(true, "parentChangeId", 1)]
    [Arguments(false, "changeId", 1)]
    [Arguments(false, "parentChangeId", 1)]
    [Arguments(true, "changeId", -DraftSave.ChangeIdMaxLength)]
    [Arguments(false, "parentChangeId", -DraftSave.ChangeIdMaxLength)]
    public async Task an_id_that_is_too_long_or_empty_gives_422_and_saves_nothing(bool raw, string field, int extra)
    {
        // Arrange
        var session = await AddSessionOfNewPersonAsync();
        var id = new string('a', DraftSave.ChangeIdMaxLength + extra);
        var body = new Dictionary<string, object> { ["text"] = "words", ["baseRevision"] = 0, ["savedAt"] = DateTimeOffset.UtcNow, [field] = id };

        // Act
        var reply = await FactoryClient.PutAsJsonAsync(RouteOf(session, raw), body);
        var problem = await reply.Content.ReadFromJsonAsync<JsonElement>();
        var page = await FactoryClient.GetFromJsonAsync<RevisionPageDto>(RevisionsOf(session, raw));

        // Assert
        reply.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        problem.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        page!.Items.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-46")]
    public async Task an_id_of_the_longest_length_is_kept_in_full()
    {
        // Arrange
        var session = await AddSessionOfNewPersonAsync();
        var id = new string('a', DraftSave.ChangeIdMaxLength);

        // Act
        var won = await SaveAsync(RouteOf(session, raw: true), new { text = "words", baseRevision = 0, savedAt = DateTimeOffset.UtcNow, changeId = id, parentChangeId = id });
        var revision = await FactoryClient.GetFromJsonAsync<RevisionDto>($"{RevisionsOf(session, raw: true)}/{won.DraftRevision}");

        // Assert
        (revision!.ChangeId, revision.ParentChangeId).ShouldBe((id, id));
    }

    private async Task<SessionDto> AddSessionOfNewPersonAsync()
    {
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        return await FactoryClient.AddSessionAsync();
    }

    private static string RouteOf(SessionDto session, bool raw)
        => raw ? ApiRoutes.Sessions.Draft(session.Id) : ApiRoutes.Sessions.Cleaned(session.Id);

    private static string RevisionsOf(SessionDto session, bool raw)
        => raw ? ApiRoutes.Sessions.RawRevisions(session.Id) : ApiRoutes.Sessions.CleanedRevisions(session.Id);

    private async Task<DraftSaveResultDto> SaveAsync(string route, object body)
    {
        var reply = await FactoryClient.PutAsJsonAsync(route, body);
        reply.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await reply.Content.ReadFromJsonAsync<DraftSaveResultDto>()).ShouldNotBeNull();
    }
}
