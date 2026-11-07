namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.Sessions.Dtos;
using Shouldly;
using TestUtilities;

public class DraftSaveRowTests : TestBase
{
    [Test]
    [Property("Rule", "SES-8")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task a_replay_of_a_loss_from_a_fast_clock_says_lost_and_makes_no_revision(bool raw)
    {
        // Arrange
        var session = await AddSessionOfNewPersonAsync();
        var route = RouteOf(session, raw);
        await SaveAsync(route, "words of the Draft", 0, DateTimeOffset.UtcNow);
        var fast = DateTimeOffset.UtcNow.AddHours(5);
        var first = await SaveAsync(route, "words of the fast phone", 99, fast);

        // Act
        var replay = await SaveAsync(route, "words of the fast phone", 99, fast);

        // Assert
        first.Outcome.ShouldBe("Lost");
        replay.Outcome.ShouldBe("Lost");
        replay.LatestRevision.ShouldBe(first.LatestRevision);
    }

    [Test]
    [Property("Rule", "SES-8")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task the_same_words_in_another_session_do_not_answer_a_save(bool raw)
    {
        // Arrange
        var other = await AddSessionOfNewPersonAsync();
        var session = await FactoryClient.AddSessionAsync();
        var now = DateTimeOffset.UtcNow;
        await SaveAsync(RouteOf(other, raw), "the same words", 0, now.AddMinutes(-3));
        var route = RouteOf(session, raw);
        await SaveAsync(route, "other words", 0, now.AddMinutes(-2));

        // Act
        var result = await SaveAsync(route, "the same words", 1, now.AddMinutes(-1));
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        result.Outcome.ShouldBe("Won");
        result.DraftRevision.ShouldBe(2);
        (raw ? read!.RawText : read!.CleanedText).ShouldBe("the same words");
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
