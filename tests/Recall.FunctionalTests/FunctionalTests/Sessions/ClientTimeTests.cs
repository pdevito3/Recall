namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.Sessions.Dtos;
using Shouldly;
using TestUtilities;

public class ClientTimeTests : TestBase
{
    private async Task<SessionDto> SignInWithSessionAsync()
    {
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        return await FactoryClient.AddSessionAsync();
    }

    [Test]
    [Property("Rule", "SES-2")]
    [Arguments("2026-01-01T00:00:00")]
    [Arguments("2026-01-01T00:00:00.123")]
    [Arguments("2026-01-01")]
    public async Task each_journal_route_that_takes_a_time_refuses_a_time_with_no_offset(string time)
    {
        // Arrange
        var session = await SignInWithSessionAsync();

        // Act
        var create = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = time });
        var raw = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Draft(session.Id), new { text = "words", baseRevision = 0, savedAt = time });
        var cleaned = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Cleaned(session.Id), new { text = "words", baseRevision = 0, savedAt = time });
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        create.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        raw.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        cleaned.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        read!.RawDraftRevision.ShouldBe(0);
    }

    [Test]
    [Property("Rule", "SES-2")]
    [Arguments("2026-01-01T00:00:00Z", "2026-01-01T00:00:00+00:00")]
    [Arguments("2026-01-01T02:00:00+02:00", "2026-01-01T00:00:00+00:00")]
    [Arguments("2025-12-31T19:00:00.5-05:00", "2026-01-01T00:00:00.5+00:00")]
    public async Task a_time_with_an_offset_is_that_instant(string time, string instant)
    {
        // Arrange
        await SignInWithSessionAsync();

        // Act
        var create = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = time });

        // Assert
        create.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await create.Content.ReadFromJsonAsync<SessionDto>())!.StartedAt.ShouldBe(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    [Property("Rule", "SES-2")]
    [Arguments("0001-01-01T00:00:00+14:00")]
    [Arguments("9999-12-31T23:59:59-14:00")]
    [Arguments("not a time")]
    public async Task a_time_that_is_not_one_gives_400(string time)
    {
        // Arrange
        await SignInWithSessionAsync();

        // Act
        var create = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = time });

        // Assert
        create.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
