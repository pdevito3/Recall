namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Content;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.UserSettings.Dtos;
using Shouldly;
using Sessions;
using TestUtilities;

public class TwoPeopleOfOneNameTests : TestBase
{
    private static readonly string[] Jane = ["Jane"];

    [Test]
    [Property("Rule", "SES-56")]
    public async Task with_approval_on_two_janes_give_one_proposal_with_both()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var (park, doe) = await TwoJanesAsync();
        var session = await scenario.SessionWithWordsAsync("raw words");

        // Act
        var stored = await PostAsync(ApiRoutes.Sessions.CleanupResult(session.Id), new { cleanedMarkdown = "Coffee with Jane", peopleProposal = Jane, baseCleanedRevision = 0, baseRawRevision = 1 });

        // Assert
        var proposal = stored.PeopleProposals.ShouldHaveSingleItem();
        proposal.Label.ShouldBe("Jane");
        proposal.MatchedPersonIds.ShouldBe([park.Id, doe.Id], ignoreOrder: true);
        proposal.IsNew.ShouldBeFalse();
        PersonMentions.PersonIdsIn(stored.CleanedText).ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task with_approval_off_two_janes_give_no_tag_and_a_proposal()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await TurnApprovalOffAsync();
        var (park, doe) = await TwoJanesAsync();
        var session = await scenario.SessionWithWordsAsync("raw words");

        // Act
        var stored = await PostAsync(ApiRoutes.Sessions.CleanupResult(session.Id), new { cleanedMarkdown = "Coffee with Jane", peopleProposal = Jane, baseCleanedRevision = 0, baseRawRevision = 1 });

        // Assert
        PersonMentions.PersonIdsIn(stored.CleanedText).ShouldBeEmpty();
        stored.PeopleProposals.ShouldHaveSingleItem().MatchedPersonIds.ShouldBe([park.Id, doe.Id], ignoreOrder: true);
        (await PeopleCountAsync(scenario)).ShouldBe(2);
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task an_approve_with_no_choice_between_two_janes_gets_422_and_writes_nothing()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await TwoJanesAsync();
        var session = await scenario.SessionWithWordsAsync("raw words");
        var stored = await PostAsync(ApiRoutes.Sessions.CleanupResult(session.Id), new { cleanedMarkdown = "Coffee with Jane", peopleProposal = Jane, baseCleanedRevision = 0, baseRawRevision = 1 });
        var revisions = await CleanupScenario.CleanedRevisionCountAsync(session.Id);

        // Act
        var answer = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Jane", approve = true, createNew = false });

        // Assert
        answer.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await answer.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").GetProperty("bindToPersonId")[0].GetString().ShouldBe(PersonProposalApproval.AskWhichMessage);
        var after = await scenario.ReadAsync(session.Id);
        after.PeopleProposals.ShouldHaveSingleItem().MatchedPersonIds.ShouldBe(stored.PeopleProposals.ShouldHaveSingleItem().MatchedPersonIds);
        after.CleanedText.ShouldBe(stored.CleanedText);
        after.Version.ShouldBe(stored.Version);
        (await CleanupScenario.CleanedRevisionCountAsync(session.Id)).ShouldBe(revisions);
        (await PeopleCountAsync(scenario)).ShouldBe(2);
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task a_bind_to_the_second_jane_tags_that_jane()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var (park, doe) = await TwoJanesAsync();
        var session = await scenario.SessionWithWordsAsync("raw words");
        await PostAsync(ApiRoutes.Sessions.CleanupResult(session.Id), new { cleanedMarkdown = "Coffee with Jane", peopleProposal = Jane, baseCleanedRevision = 0, baseRawRevision = 1 });

        // Act
        var bound = await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Jane", approve = true, bindToPersonId = doe.Id });

        // Assert
        PersonMentions.PersonIdsIn(bound.CleanedText).ShouldBe([doe.Id]);
        bound.CleanedText.ShouldContain("\"label\":\"Jane\"");
        bound.CleanedText.ShouldNotContain(park.Id);
        bound.PeopleProposals.ShouldBeEmpty();
        (await PeopleCountAsync(scenario)).ShouldBe(2);
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task an_add_of_a_new_person_from_a_proposal_of_two_janes_tags_a_third()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var (park, doe) = await TwoJanesAsync();
        var session = await scenario.SessionWithWordsAsync("raw words");
        await PostAsync(ApiRoutes.Sessions.CleanupResult(session.Id), new { cleanedMarkdown = "Coffee with Jane", peopleProposal = Jane, baseCleanedRevision = 0, baseRawRevision = 1 });

        // Act
        var added = await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Jane", approve = true, createNew = true });

        // Assert
        var tagged = PersonMentions.PersonIdsIn(added.CleanedText).ShouldHaveSingleItem();
        tagged.ShouldNotBeOneOf(park.Id, doe.Id);
        (await PeopleCountAsync(scenario)).ShouldBe(3);
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task a_read_of_the_session_lists_a_second_jane_added_after_the_cleanup()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var park = await AddAsync(new { label = "Jane", fullName = "Jane Park" });
        var session = await SessionNamingAsync(scenario, Jane);
        var doe = await AddAsync(new { label = "Jane", fullName = "Jane Doe" });

        // Act
        var read = await scenario.ReadAsync(session.Id);

        // Assert
        var proposal = read.PeopleProposals.ShouldHaveSingleItem();
        proposal.MatchedPersonIds.ShouldBe([park.Id, doe.Id]);
        proposal.IsNew.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task a_proposal_with_no_match_lists_the_janes_added_later_and_is_not_new()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await SessionNamingAsync(scenario, Jane);
        var (park, doe) = await TwoJanesAsync();

        // Act
        var read = await scenario.ReadAsync(session.Id);

        // Assert
        var proposal = read.PeopleProposals.ShouldHaveSingleItem();
        proposal.MatchedPersonIds.ShouldBe([park.Id, doe.Id], ignoreOrder: true);
        proposal.IsNew.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task a_hand_tag_of_a_jane_added_after_the_cleanup_settles_the_proposal()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await AddAsync(new { label = "Jane", fullName = "Jane Park" });
        var session = await SessionNamingAsync(scenario, Jane);
        var doe = await AddAsync(new { label = "Jane", fullName = "Jane Doe" });
        var mention = $$$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Coffee with "},{"type":"mention","attrs":{"personId":"{{{doe.Id}}}","label":"Jane"}}]}]}""";

        // Act
        var saved = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Cleaned(session.Id), new { text = mention, baseRevision = session.CleanedDraftRevision, savedAt = DateTimeOffset.UtcNow });

        // Assert
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var read = await scenario.ReadAsync(session.Id);
        read.PersonIds.ShouldBe([doe.Id]);
        read.PeopleProposals.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task the_reply_of_an_approve_lists_the_live_matches_of_the_other_proposals()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var park = await AddAsync(new { label = "Jane", fullName = "Jane Park" });
        var session = await SessionNamingAsync(scenario, ["Jane", "Kim"], "Coffee with Jane and Kim");
        var doe = await AddAsync(new { label = "Jane", fullName = "Jane Doe" });
        var kim = await AddAsync(new { label = "Kim" });

        // Act
        var reply = await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Kim", approve = true });

        // Assert
        PersonMentions.PersonIdsIn(reply.CleanedText).ShouldBe([kim.Id]);
        var jane = reply.PeopleProposals.ShouldHaveSingleItem();
        jane.Label.ShouldBe("Jane");
        jane.MatchedPersonIds.ShouldBe([park.Id, doe.Id]);
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task a_bind_to_the_stored_jane_after_a_second_jane_came_tags_her()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var park = await AddAsync(new { label = "Jane", fullName = "Jane Park" });
        var session = await SessionNamingAsync(scenario, Jane);
        await AddAsync(new { label = "Jane", fullName = "Jane Doe" });

        // Act
        var bound = await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Jane", approve = true, bindToPersonId = park.Id });

        // Assert
        PersonMentions.PersonIdsIn(bound.CleanedText).ShouldBe([park.Id]);
        bound.PeopleProposals.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task a_person_of_the_name_added_after_the_device_pulled_the_session_sends_the_session_again()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var park = await AddAsync(new { label = "Jane", fullName = "Jane Park" });
        var session = await SessionNamingAsync(scenario, Jane);
        var held = await FactoryClient.PullAsync();
        var doe = await AddAsync(new { label = "Jane", fullName = "Jane Doe" });

        // Act
        var page = await FactoryClient.PullAsync(held.Cursor);

        // Assert
        page.People.ShouldHaveSingleItem().Id.ShouldBe(doe.Id);
        var sent = page.Sessions.ShouldHaveSingleItem();
        sent.Id.ShouldBe(session.Id);
        sent.PeopleProposals.ShouldHaveSingleItem().MatchedPersonIds.ShouldBe([park.Id, doe.Id]);
        sent.Version.ShouldBe(session.Version);
        sent.MetadataVersion.ShouldBe(session.MetadataVersion);
        (await FactoryClient.PullAsync(page.Cursor)).Sessions.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task a_pull_from_the_start_sends_a_session_once_in_one_page_and_each_copy_of_a_paged_pull_is_live()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await AddAsync(new { label = "Jane", fullName = "Jane Park" });
        var session = await SessionNamingAsync(scenario, Jane);
        await AddAsync(new { label = "Jane", fullName = "Jane Doe" });

        // Act
        var onePage = await FactoryClient.PullAsync();
        var paged = new List<SyncSessionDto>();
        string? cursor = null;
        bool hasMore;
        do
        {
            var page = await FactoryClient.PullAsync(cursor, pageSize: 1);
            paged.AddRange(page.Sessions);
            cursor = page.Cursor;
            hasMore = page.HasMore;
        }
        while (hasMore);

        // Assert
        onePage.Sessions.ShouldHaveSingleItem().PeopleProposals.ShouldHaveSingleItem().MatchedPersonIds.Count.ShouldBe(2);
        paged.Where(s => s.Id == session.Id).ShouldNotBeEmpty();
        paged.Where(s => s.Id == session.Id).ShouldAllBe(s => s.PeopleProposals.Single().MatchedPersonIds.Count == 2);
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task a_person_of_another_name_sends_no_session_again()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await AddAsync(new { label = "Jane", fullName = "Jane Park" });
        await SessionNamingAsync(scenario, Jane);
        var held = await FactoryClient.PullAsync();
        await AddAsync(new { label = "Sam" });

        // Act
        var page = await FactoryClient.PullAsync(held.Cursor);

        // Assert
        page.People.ShouldHaveSingleItem().Label.ShouldBe("Sam");
        page.Sessions.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-56")]
    public async Task a_session_on_a_later_page_is_not_sent_again_early()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var park = await AddAsync(new { label = "Jane", fullName = "Jane Park" });
        var session = await SessionNamingAsync(scenario, Jane);
        var held = await FactoryClient.PullAsync();
        var doe = await AddAsync(new { label = "Jane", fullName = "Jane Doe" });
        var saved = await FactoryClient.SaveDraftAsync(session.Id, "more raw words", session.RawDraftRevision);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());

        // Act
        var first = await FactoryClient.PullAsync(held.Cursor, pageSize: 1);
        var second = await FactoryClient.PullAsync(first.Cursor, pageSize: 1);

        // Assert
        first.People.ShouldHaveSingleItem().Id.ShouldBe(doe.Id);
        first.Sessions.ShouldBeEmpty();
        var sent = second.Sessions.ShouldHaveSingleItem();
        sent.RawText.ShouldBe("more raw words");
        sent.PeopleProposals.ShouldHaveSingleItem().MatchedPersonIds.ShouldBe([park.Id, doe.Id]);
    }

    private async Task<SessionDto> SessionNamingAsync(CleanupScenario scenario, string[] names, string cleaned = "Coffee with Jane")
    {
        var session = await scenario.SessionWithWordsAsync("raw words");
        return await PostAsync(ApiRoutes.Sessions.CleanupResult(session.Id), new { cleanedMarkdown = cleaned, peopleProposal = names, baseCleanedRevision = 0, baseRawRevision = 1 });
    }

    private async Task<(PersonDto Park, PersonDto Doe)> TwoJanesAsync()
        => (await AddAsync(new { label = "Jane", fullName = "Jane Park", notes = "Sam's teacher" }), await AddAsync(new { label = "Jane", fullName = "Jane Doe" }));

    private async Task<PersonDto> AddAsync(object body)
    {
        var answer = await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, body);
        answer.StatusCode.ShouldBe(HttpStatusCode.Created, await answer.Content.ReadAsStringAsync());
        return (await answer.Content.ReadFromJsonAsync<PersonDto>()).ShouldNotBeNull();
    }

    private async Task TurnApprovalOffAsync()
    {
        var settings = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new UserSettingChangeDto { TimeZoneMode = "Fixed", TimeZone = "Europe/Tallinn", LocationCaptureEnabled = false, RequirePeopleProposalApproval = false, Version = 0 });
        settings.StatusCode.ShouldBe(HttpStatusCode.OK, await settings.Content.ReadAsStringAsync());
    }

    private static Task<int> PeopleCountAsync(CleanupScenario scenario)
        => ExecuteDbContextAsync(db => db.People.IgnoreQueryFilters().CountAsync(person => person.UserId == scenario.Member.Id && !person.IsDeleted));

    private async Task<SessionDto> PostAsync(string route, object body)
    {
        var answer = await FactoryClient.PostAsJsonAsync(route, body);
        answer.StatusCode.ShouldBe(HttpStatusCode.OK, await answer.Content.ReadAsStringAsync());
        return (await answer.Content.ReadFromJsonAsync<SessionDto>()).ShouldNotBeNull();
    }
}
