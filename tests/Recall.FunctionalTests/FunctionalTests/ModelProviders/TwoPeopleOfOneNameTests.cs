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
using TestUtilities;

/// <summary>Two People of one label, two Janes. Cleanup never picks one of them for the person.</summary>
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
