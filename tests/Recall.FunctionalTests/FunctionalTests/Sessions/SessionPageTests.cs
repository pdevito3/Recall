namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Dtos;
using Shouldly;
using TestUtilities;

public class SessionPageTests : TestBase
{
    [Test]
    [Property("Rule", "SES-28")]
    public async Task paging_gives_each_session_once_newest_first_also_with_equal_start_times()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var expected = new List<string>();
        for (var i = 0; i < 7; i++)
            expected.Add((await FactoryClient.AddSessionAsync(JournalScenario.Morning.AddHours(-(i / 3)))).Id);

        // Act
        var seen = new List<SessionSummaryDto>();
        string? cursor = null;
        do
        {
            var url = $"{ApiRoutes.Sessions.Root}?pageSize=2" + (cursor is null ? "" : $"&cursor={cursor}");
            var page = (await FactoryClient.GetFromJsonAsync<SessionPageDto>(url))!;
            page.Items.Count.ShouldBeLessThanOrEqualTo(2);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        // Assert
        seen.Select(s => s.Id).ShouldBe(expected, ignoreOrder: true);
        seen.Select(s => s.StartedAt).ShouldBeInOrder(SortDirection.Descending);
    }

    [Test]
    [Property("Rule", "SES-28")]
    public async Task a_session_that_arrives_between_two_pages_moves_no_row()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var oldest = await FactoryClient.AddSessionAsync(JournalScenario.Morning.AddHours(-2));
        var middle = await FactoryClient.AddSessionAsync(JournalScenario.Morning.AddHours(-1));
        var first = (await FactoryClient.GetFromJsonAsync<SessionPageDto>($"{ApiRoutes.Sessions.Root}?pageSize=1"))!;

        // Act
        await FactoryClient.AddSessionAsync(JournalScenario.Morning);
        var second = (await FactoryClient.GetFromJsonAsync<SessionPageDto>($"{ApiRoutes.Sessions.Root}?pageSize=1&cursor={first.NextCursor}"))!;

        // Assert
        first.Items.ShouldHaveSingleItem().Id.ShouldBe(middle.Id);
        second.Items.ShouldHaveSingleItem().Id.ShouldBe(oldest.Id);
        second.NextCursor.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "SES-23")]
    public async Task a_timeline_row_has_the_preview_and_no_draft()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var words = new string('x', Session.PreviewMaxLength + 50);
        await FactoryClient.SaveDraftAsync(session.Id, words, 0);

        // Act
        var body = await FactoryClient.GetStringAsync(ApiRoutes.Sessions.Root);

        // Assert
        body.ShouldContain(new string('x', Session.PreviewMaxLength));
        body.ShouldNotContain(words);
    }

    [Test]
    [Arguments("not-a-cursor")]
    [Arguments("123_usr_1")]
    public async Task a_cursor_that_the_server_did_not_make_gives_400(string cursor)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var response = await FactoryClient.GetAsync($"{ApiRoutes.Sessions.Root}?cursor={cursor}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
