namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.Sessions.Dtos;
using Shouldly;
using TestUtilities;

public class DraftSaveRuleTests : TestBase
{
    private const int OneHourInSeconds = 3600;

    [Test]
    [Property("Rule", "SES-5")]
    [Arguments(true, 0, "newest words", "older copy")]
    [Arguments(true, 0, "older copy", "newest words")]
    [Arguments(true, OneHourInSeconds, "newest words", "older copy")]
    [Arguments(true, OneHourInSeconds, "older copy", "newest words")]
    [Arguments(true, -OneHourInSeconds, "newest words", "older copy")]
    [Arguments(true, -OneHourInSeconds, "older copy", "newest words")]
    [Arguments(false, 0, "newest words", "older copy")]
    [Arguments(false, 0, "older copy", "newest words")]
    [Arguments(false, OneHourInSeconds, "newest words", "older copy")]
    [Arguments(false, OneHourInSeconds, "older copy", "newest words")]
    [Arguments(false, -OneHourInSeconds, "newest words", "older copy")]
    [Arguments(false, -OneHourInSeconds, "older copy", "newest words")]
    public async Task of_two_saves_from_one_base_only_the_first_to_arrive_wins_at_any_clock(bool raw, int secondsOfTheLaterSave, string first, string second)
    {
        // Arrange
        var session = await AddSessionOfNewPersonAsync();
        var route = RouteOf(session, raw);
        var now = DateTimeOffset.UtcNow;
        var start = await SaveAsync(route, "start", 0, now);

        // Act
        var won = await SaveAsync(route, first, start.DraftRevision!.Value, now.AddSeconds(1));
        var lost = await SaveAsync(route, second, start.DraftRevision!.Value, now.AddSeconds(2 + secondsOfTheLaterSave));
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));
        var row = await FactoryClient.GetFromJsonAsync<RevisionDto>($"{(raw ? ApiRoutes.Sessions.RawRevisions(session.Id) : ApiRoutes.Sessions.CleanedRevisions(session.Id))}/{lost.LatestRevision}");

        // Assert
        won.Outcome.ShouldBe("Won");
        lost.Outcome.ShouldBe("Lost");
        lost.DraftRevision.ShouldBeNull();
        (raw ? read!.RawText : read!.CleanedText).ShouldBe(first);
        row!.Text.ShouldBe(second);
    }

    [Test]
    [Property("Rule", "SES-8")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task a_replay_of_a_win_says_kept_also_when_the_draft_still_holds_the_words(bool raw)
    {
        // Arrange
        var session = await AddSessionOfNewPersonAsync();
        var route = RouteOf(session, raw);
        var now = DateTimeOffset.UtcNow;
        var won = await SaveAsync(route, "words", 0, now);

        // Act
        var replay = await SaveAsync(route, "words", 0, now.ToOffset(TimeSpan.FromHours(2)));

        // Assert
        won.Outcome.ShouldBe("Won");
        replay.Outcome.ShouldBe("Kept");
        replay.DraftRevision.ShouldBe(won.DraftRevision);
        replay.LatestRevision.ShouldBe(won.LatestRevision);
    }

    [Test]
    [Property("Rule", "SES-8")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task a_replay_of_a_loss_says_lost_also_when_the_draft_now_holds_the_words(bool raw)
    {
        // Arrange
        var session = await AddSessionOfNewPersonAsync();
        var route = RouteOf(session, raw);
        var now = DateTimeOffset.UtcNow;
        await SaveAsync(route, "start", 0, now);
        var first = await SaveAsync(route, "the words", 0, now.AddSeconds(1));
        await SaveAsync(route, "the words", 1, now.AddSeconds(2));

        // Act
        var replay = await SaveAsync(route, "the words", 0, now.AddSeconds(1));

        // Assert
        first.Outcome.ShouldBe("Lost");
        replay.Outcome.ShouldBe("Lost");
        replay.DraftRevision.ShouldBeNull();
        replay.LatestRevision.ShouldBe(3);
    }

    [Test]
    [Property("Rule", "SES-7")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task a_revision_and_the_draft_hold_the_time_that_the_client_sent_also_a_time_in_the_future(bool raw)
    {
        // Arrange
        var session = await AddSessionOfNewPersonAsync();
        var fast = DateTimeOffset.UtcNow.AddHours(5);

        // Act
        var won = await SaveAsync(RouteOf(session, raw), "words", 0, fast);
        var row = await FactoryClient.GetFromJsonAsync<RevisionDto>($"{(raw ? ApiRoutes.Sessions.RawRevisions(session.Id) : ApiRoutes.Sessions.CleanedRevisions(session.Id))}/{won.DraftRevision}");

        // Assert
        won.SavedAt.ShouldBe(fast);
        row!.SavedAt.ShouldBe(fast);
    }

    [Test]
    [Property("Rule", "SES-5")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task a_slow_save_on_the_current_draft_moves_the_save_time_back_and_still_wins(bool raw)
    {
        // Arrange
        var session = await AddSessionOfNewPersonAsync();
        var route = RouteOf(session, raw);
        var now = DateTimeOffset.UtcNow;
        var start = await SaveAsync(route, "start", 0, now);

        // Act
        var slow = await SaveAsync(route, "from a slow clock", start.DraftRevision!.Value, now.AddHours(-3));

        // Assert
        slow.Outcome.ShouldBe("Won");
        slow.SavedAt.ShouldBe(now.AddHours(-3));
    }

    private async Task<SessionDto> AddSessionOfNewPersonAsync()
    {
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        return await FactoryClient.AddSessionAsync();
    }

    private static string RouteOf(SessionDto session, bool raw)
        => raw ? ApiRoutes.Sessions.Draft(session.Id) : ApiRoutes.Sessions.Cleaned(session.Id);

    private async Task<DraftSaveResultDto> SaveAsync(string route, string text, int baseRevision, DateTimeOffset savedAt)
    {
        var reply = await FactoryClient.PutAsJsonAsync(route, new { text, baseRevision, savedAt });
        reply.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await reply.Content.ReadFromJsonAsync<DraftSaveResultDto>()).ShouldNotBeNull();
    }
}
