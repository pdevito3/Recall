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
    [Arguments(true, 0)]
    [Arguments(true, OneHourInSeconds)]
    [Arguments(true, -OneHourInSeconds)]
    [Arguments(false, 0)]
    [Arguments(false, OneHourInSeconds)]
    [Arguments(false, -OneHourInSeconds)]
    public async Task of_two_saves_from_one_base_only_the_first_to_arrive_wins_at_any_clock(bool raw, int secondsOfTheLaterSave)
    {
        foreach (var (first, second) in new[] { ("newest words", "older copy"), ("older copy", "newest words") })
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
