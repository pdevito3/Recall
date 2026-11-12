namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using Shouldly;
using TestUtilities;

public class SyncFeedTests : TestBase
{
    [Test]
    [Property("Rule", "SES-16")]
    public async Task a_pull_from_a_cursor_gives_only_the_new_changes()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var before = await FactoryClient.AddSessionAsync();
        var first = await FactoryClient.PullAsync();
        var after = await FactoryClient.AddSessionAsync();

        // Act
        var second = await FactoryClient.PullAsync(first.Cursor);
        var third = await FactoryClient.PullAsync(second.Cursor);

        // Assert
        first.Sessions.ShouldHaveSingleItem().Id.ShouldBe(before.Id);
        second.Sessions.ShouldHaveSingleItem().Id.ShouldBe(after.Id);
        third.Sessions.ShouldBeEmpty();
        third.Cursor.ShouldBe(second.Cursor);
    }

    [Test]
    [Property("Rule", "SES-16")]
    public async Task saves_with_one_timestamp_each_arrive_once_across_pages()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var session = await FactoryClient.AddSessionAsync();
            await FactoryClient.SaveDraftAsync(session.Id, $"words {i}", 0, JournalScenario.Morning);
            ids.Add(session.Id);
        }

        // Act
        var seen = new List<string>();
        string? cursor = null;
        bool hasMore;
        do
        {
            var page = await FactoryClient.PullAsync(cursor, pageSize: 2);
            page.Sessions.Count.ShouldBeLessThanOrEqualTo(2);
            seen.AddRange(page.Sessions.Select(s => s.Id));
            cursor = page.Cursor;
            hasMore = page.HasMore;
        }
        while (hasMore);

        // Assert
        seen.ShouldBe(ids, ignoreOrder: true);
        seen.ShouldBeUnique();
    }

    [Test]
    [Property("Rule", "SES-16")]
    public async Task a_row_that_changed_twice_arrives_once_in_its_newest_state()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        await FactoryClient.SaveDraftAsync(session.Id, "first", 0);
        await FactoryClient.SaveDraftAsync(session.Id, "second", 1);

        // Act
        var page = await FactoryClient.PullAsync();

        // Assert
        page.Sessions.ShouldHaveSingleItem().RawText.ShouldBe("second");
    }

    [Test]
    [Property("Rule", "SES-12")]
    public async Task a_delete_arrives_as_a_tombstone_with_the_two_ids_and_the_row_is_gone()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var before = await FactoryClient.PullAsync();

        // Act
        await FactoryClient.DeleteAsync(ApiRoutes.Sessions.GetRecord(session.Id));
        var afterDelete = await FactoryClient.PullAsync(before.Cursor);
        var fromTheStart = await FactoryClient.PullAsync();

        // Assert
        afterDelete.Sessions.ShouldBeEmpty();
        var tombstone = afterDelete.Tombstones.ShouldHaveSingleItem();
        tombstone.EntityType.ShouldBe("Session");
        tombstone.Id.ShouldBe(session.Id);
        tombstone.ClientSessionId.ShouldBe(session.ClientSessionId);
        fromTheStart.Sessions.ShouldBeEmpty();
        fromTheStart.Tombstones.ShouldHaveSingleItem().Id.ShouldBe(session.Id);
    }

    [Test]
    [Property("Rule", "SES-16")]
    public async Task the_feed_has_people_corrections_and_settings()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label = "Sam" });
        await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, new { canonicalTerm = "Kubernetes" });
        await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new
        {
            timeZoneMode = "Fixed",
            timeZone = "Europe/Tallinn",
            locationCaptureEnabled = false,
            requirePeopleProposalApproval = true,
            version = 0,
        });

        // Act
        var page = await FactoryClient.PullAsync();

        // Assert
        page.People.ShouldHaveSingleItem().Label.ShouldBe("Sam");
        page.Corrections.ShouldHaveSingleItem().CanonicalTerm.ShouldBe("Kubernetes");
        page.Settings!.TimeZone.ShouldBe("Europe/Tallinn");
    }

    [Test]
    [Property("Rule", "SES-17")]
    public async Task a_cursor_ahead_of_the_server_gives_422()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var page = await FactoryClient.PullAsync();
        await FactoryClient.AddSessionAsync();

        // Act
        var response = await FactoryClient.GetAsync($"{ApiRoutes.Sync.Changes}?cursor={long.MaxValue}");

        // Assert
        page.Cursor.ShouldBe("0");
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    [Property("Rule", "SES-17")]
    public async Task a_cursor_that_is_not_a_sequence_gives_400()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var response = await FactoryClient.GetAsync($"{ApiRoutes.Sync.Changes}?cursor=abc");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
