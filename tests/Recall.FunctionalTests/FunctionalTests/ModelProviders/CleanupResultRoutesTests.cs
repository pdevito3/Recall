namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recall.FunctionalTests.FunctionalTests.Sessions;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.UserSettings.Dtos;
using Shouldly;
using TestUtilities;

/// <summary>Rows 14, 15, and 19 of docs/ios-api-changes.md.</summary>
public class CleanupResultRoutesTests : TestBase
{
    private static readonly string[] HeldTopics = ["work"];
    private static readonly string[] Misheard = ["cooper netties"];

    private static string ReplyWith(string cleaned, string[]? topics = null, string[]? moods = null, string[]? people = null) => JsonSerializer.Serialize(new
    {
        cleanedMarkdown = cleaned,
        synopsis = "I had lunch.",
        topicSuggestions = topics ?? [],
        moodSuggestions = moods ?? [],
        peopleProposal = people ?? [],
    });

    private static async Task<SessionDto> CleanedSessionAsync(CleanupScenario scenario, string reply)
    {
        scenario.Fake.Behavior.Reply = reply;
        var started = await scenario.SessionWithRunningCleanupAsync("raw words about lunch");
        await scenario.RunJobAsync(started.Id);
        return await scenario.ReadAsync(started.Id);
    }

    private async Task<SessionDto> PostAsync(string route, object body)
    {
        var answer = await FactoryClient.PostAsJsonAsync(route, body);
        answer.StatusCode.ShouldBe(HttpStatusCode.OK, await answer.Content.ReadAsStringAsync());
        return (await answer.Content.ReadFromJsonAsync<SessionDto>()).ShouldNotBeNull();
    }

    [Test]
    [Property("Rule", "SES-39")]
    [Property("Rule", "SES-30")]
    public async Task an_accepted_topic_is_an_ai_topic_and_a_retry_changes_nothing()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await CleanedSessionAsync(scenario, ReplyWith("Lunch", topics: ["work", "food"]));

        // Act
        var accepted = await PostAsync(ApiRoutes.Sessions.AcceptSuggestion(session.Id), new { kind = "Topic", value = "work" });
        var retried = await PostAsync(ApiRoutes.Sessions.AcceptSuggestion(session.Id), new { kind = "Topic", value = "work" });

        // Assert
        accepted.Topics.ShouldHaveSingleItem().Name.ShouldBe("work");
        accepted.Suggestions.ShouldHaveSingleItem().Value.ShouldBe("food");
        accepted.MetadataVersion.ShouldBe(session.MetadataVersion + 1);
        retried.MetadataVersion.ShouldBe(accepted.MetadataVersion);
        retried.Topics.Count.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "SES-39")]
    [Property("Rule", "SES-30")]
    public async Task the_ai_writer_adds_no_topic_that_the_user_holds()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        scenario.Fake.Behavior.Reply = ReplyWith("Lunch", topics: ["Work", "food"]);
        var session = await scenario.SessionWithWordsAsync("raw words");
        var metadata = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { userTopics = HeldTopics, metadataVersion = 0 });
        metadata.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.PostAsync(ApiRoutes.Sessions.Cleanup(session.Id), null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Act
        await scenario.RunJobAsync(session.Id);
        var cleaned = await scenario.ReadAsync(session.Id);

        // Assert
        cleaned.Suggestions.ShouldHaveSingleItem().Value.ShouldBe("food");
        cleaned.Topics.ShouldHaveSingleItem().Name.ShouldBe("work");
    }

    [Test]
    public async Task a_rejected_suggestion_goes_away_and_a_mood_can_be_accepted()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await CleanedSessionAsync(scenario, ReplyWith("Lunch", topics: ["work"], moods: ["calm", "tired"]));

        // Act
        var rejected = await PostAsync(ApiRoutes.Sessions.RejectSuggestion(session.Id), new { kind = "Topic", value = "work" });
        var accepted = await PostAsync(ApiRoutes.Sessions.AcceptSuggestion(session.Id), new { kind = "Mood", value = "Calm" });
        var rejectedAgain = await PostAsync(ApiRoutes.Sessions.RejectSuggestion(session.Id), new { kind = "Topic", value = "work" });

        // Assert
        rejected.Topics.ShouldBeEmpty();
        accepted.Moods.ShouldHaveSingleItem().ShouldBe("Calm");
        accepted.Suggestions.ShouldHaveSingleItem().Value.ShouldBe("Tired");
        rejectedAgain.Suggestions.ShouldHaveSingleItem().Value.ShouldBe("Tired");
    }

    [Test]
    public async Task a_suggestion_of_an_unknown_kind_gets_422()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await CleanedSessionAsync(scenario, ReplyWith("Lunch", topics: ["work"]));

        // Act
        var answer = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.AcceptSuggestion(session.Id), new { kind = "Person", value = "work" });

        // Assert
        answer.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    [Property("Rule", "SES-40")]
    public async Task an_approved_proposal_adds_a_person_and_the_mention_in_cleaned_and_raw_stays()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await CleanedSessionAsync(scenario, ReplyWith("I had lunch with Sam.", people: ["Sam"]));
        var proposal = session.PeopleProposals.ShouldHaveSingleItem();

        // Act
        var approved = await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Sam", approve = true, createNew = true });
        var retried = await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Sam", approve = true, createNew = true });

        // Assert
        proposal.IsNew.ShouldBeTrue();
        proposal.Contexts.ShouldHaveSingleItem().ShouldContain("Sam");
        approved.PeopleProposals.ShouldBeEmpty();
        approved.CleanedText.ShouldContain("mention");
        approved.CleanedLatestRevision.ShouldBe(session.CleanedLatestRevision + 1);
        approved.RawText.ShouldBe(session.RawText);
        retried.CleanedLatestRevision.ShouldBe(approved.CleanedLatestRevision);
    }

    [Test]
    public async Task a_proposal_can_bind_to_a_person_or_be_dropped()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var added = await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label = "Samuel" });
        var samuel = (await added.Content.ReadFromJsonAsync<PersonDto>()).ShouldNotBeNull();
        var session = await CleanedSessionAsync(scenario, ReplyWith("I had lunch with Sam and Kim.", people: ["Sam", "Kim"]));

        // Act
        var bound = await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Sam", approve = true, bindToPersonId = samuel.Id });
        var dropped = await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Kim", approve = false });

        // Assert
        bound.CleanedText.ShouldContain(samuel.Id);
        dropped.PeopleProposals.ShouldBeEmpty();
        dropped.CleanedLatestRevision.ShouldBe(bound.CleanedLatestRevision);
    }

    [Test]
    public async Task a_bind_to_a_person_of_another_owner_gets_404()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        FactoryClient.SignInAsAppUser(scenario.Admin.Identifier);
        var added = await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label = "Other" });
        var other = (await added.Content.ReadFromJsonAsync<PersonDto>()).ShouldNotBeNull();
        FactoryClient.SignInAsAppUser(scenario.Member.Identifier);
        var session = await CleanedSessionAsync(scenario, ReplyWith("I had lunch with Sam.", people: ["Sam"]));

        // Act
        var answer = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Sam", approve = true, bindToPersonId = other.Id });

        // Assert
        answer.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task two_proposals_of_one_name_give_one_person_and_only_a_request_gives_a_second()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var reply = ReplyWith("I had lunch with Sam.", people: ["Sam"]);
        var first = await CleanedSessionAsync(scenario, reply);
        var second = await CleanedSessionAsync(scenario, reply);
        var third = await CleanedSessionAsync(scenario, reply);

        // Act
        await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(first.Id), new { label = "Sam", approve = true, createNew = false });
        await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(second.Id), new { label = "Sam", approve = true, createNew = false });
        var afterTwo = await ExecuteDbContextAsync(db => db.People.IgnoreQueryFilters().CountAsync(person => person.Label == "Sam" && person.UserId == scenario.Member.Id));
        await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(third.Id), new { label = "Sam", approve = true, createNew = true });
        var afterRequest = await ExecuteDbContextAsync(db => db.People.IgnoreQueryFilters().CountAsync(person => person.Label == "Sam" && person.UserId == scenario.Member.Id));

        // Assert
        afterTwo.ShouldBe(1);
        afterRequest.ShouldBe(2);
    }

    [Test]
    public async Task a_run_whose_stored_form_is_too_long_adds_no_person()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var settings = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { locationCaptureEnabled = false, requirePeopleProposalApproval = false, version = 0 });
        settings.EnsureSuccessStatusCode();
        var words = string.Concat(Enumerable.Repeat("Quinn. ", Session.DraftMaxLength / 100));

        // Act
        var session = await CleanedSessionAsync(scenario, ReplyWith(words, people: ["Quinn"]));
        var people = await ExecuteDbContextAsync(db => db.People.IgnoreQueryFilters().CountAsync(person => person.Label == "Quinn" && person.UserId == scenario.Member.Id));

        // Assert
        session.CleanupFailure.ShouldBe(nameof(CleanupFailure.BadModelOutput));
        people.ShouldBe(0);
    }

    [Test]
    public async Task an_approval_that_makes_cleaned_too_long_gets_422_and_adds_no_person()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var words = string.Concat(Enumerable.Repeat("Quinn. ", Session.DraftMaxLength / 100));
        var session = await CleanedSessionAsync(scenario, ReplyWith(words, people: ["Quinn"]));

        // Act
        var answer = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Quinn", approve = true });
        var people = await ExecuteDbContextAsync(db => db.People.IgnoreQueryFilters().CountAsync(person => person.Label == "Quinn" && person.UserId == scenario.Member.Id));
        var after = await scenario.ReadAsync(session.Id);

        // Assert
        session.CleanupFailure.ShouldBe(nameof(CleanupFailure.None));
        answer.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        people.ShouldBe(0);
        after.PeopleProposals.ShouldHaveSingleItem();
    }

    [Test]
    public async Task a_proposal_whose_match_was_deleted_can_still_be_approved()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var added = await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label = "Sam" });
        var sam = (await added.Content.ReadFromJsonAsync<PersonDto>()).ShouldNotBeNull();
        var session = await CleanedSessionAsync(scenario, ReplyWith("I had lunch with Sam.", people: ["Sam"]));
        session.PeopleProposals.ShouldHaveSingleItem().MatchedPersonId.ShouldBe(sam.Id);
        await JournalScenario.ExecuteOutOfBandAsync("UPDATE people SET is_deleted = 1 WHERE id = $id", ("$id", sam.Id));

        // Act
        var approved = await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Sam", approve = true });

        // Assert
        approved.PeopleProposals.ShouldBeEmpty();
        approved.CleanedText.ShouldNotContain(sam.Id);
        approved.CleanedText.ShouldContain("mention");
    }

    [Test]
    [Property("Rule", "SES-40")]
    public async Task with_approval_off_a_cleanup_tags_the_people_at_once()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var settings = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new UserSettingChangeDto { TimeZone = "Europe/Tallinn", LocationCaptureEnabled = false, RequirePeopleProposalApproval = false, Version = 0 });
        settings.StatusCode.ShouldBe(HttpStatusCode.OK, await settings.Content.ReadAsStringAsync());

        // Act
        var session = await CleanedSessionAsync(scenario, ReplyWith("I had lunch with Sam.", people: ["Sam"]));
        var people = await FactoryClient.GetStringAsync(ApiRoutes.People.Root);

        // Assert
        session.PeopleProposals.ShouldBeEmpty();
        session.CleanedText.ShouldContain("mention");
        people.ShouldContain("Sam");
    }

    [Test]
    [Property("Rule", "SES-40")]
    public async Task a_name_that_is_not_in_cleaned_makes_no_proposal()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);

        // Act
        var session = await CleanedSessionAsync(scenario, ReplyWith("I had lunch alone.", people: ["Sam"]));

        // Assert
        session.PeopleProposals.ShouldBeEmpty();
    }

    [Test]
    public async Task a_hard_replace_correction_changes_cleaned_and_not_raw()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var correction = await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, new { canonicalTerm = "Kubernetes", mishearings = Misheard, hardReplace = true });
        correction.StatusCode.ShouldBe(HttpStatusCode.Created, await correction.Content.ReadAsStringAsync());

        // Act
        var session = await CleanedSessionAsync(scenario, ReplyWith("I set up cooper netties today, and Cooper Netties works."));

        // Assert
        session.CleanedText.ShouldContain("I set up Kubernetes today, and Kubernetes works.");
        session.RawText.ShouldContain("raw words about lunch");
    }

    [Test]
    [Property("Rule", "SES-41")]
    public async Task a_device_result_is_stored_as_a_revision_and_a_retry_adds_none()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await scenario.SessionWithWordsAsync("raw words");
        var body = new { cleanedMarkdown = "## Notes\n\nClean words", synopsis = "I wrote.", topicSuggestions = new[] { "work" }, moodSuggestions = Array.Empty<string>(), peopleProposal = Array.Empty<string>(), baseRawRevision = 1 };

        // Act
        var stored = await PostAsync(ApiRoutes.Sessions.CleanupResult(session.Id), body);
        var retried = await PostAsync(ApiRoutes.Sessions.CleanupResult(session.Id), body);

        // Assert
        stored.CleanupStatus.ShouldBe(nameof(CleanupStatus.Clean));
        stored.CleanedText.ShouldContain("Clean words");
        stored.RawLatestRevision.ShouldBe(1);
        retried.CleanedLatestRevision.ShouldBe(stored.CleanedLatestRevision);
        (await CleanupScenario.CleanedRevisionCountAsync(session.Id)).ShouldBe(1);
    }

    [Test]
    [Property("Rule", "SES-41")]
    public async Task a_device_result_while_a_job_runs_or_from_an_unknown_revision_gets_422()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await scenario.SessionWithWordsAsync("raw words");
        var running = await scenario.SessionWithRunningCleanupAsync("other words");

        // Act
        var unknownBase = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.CleanupResult(session.Id), new { cleanedMarkdown = "Clean", baseRawRevision = 9 });
        var noBase = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.CleanupResult(session.Id), new { cleanedMarkdown = "Clean", baseRawRevision = 0 });
        var whileRunning = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.CleanupResult(running.Id), new { cleanedMarkdown = "Clean", baseRawRevision = 1 });
        var blank = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.CleanupResult(session.Id), new { cleanedMarkdown = "   ", baseRawRevision = 1 });

        // Assert
        unknownBase.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        noBase.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        whileRunning.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        blank.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    public async Task a_session_with_no_words_or_no_provider_gets_422_and_stays_not_run()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient, pickModel: false);
        var withWords = await scenario.SessionWithWordsAsync("raw words");
        var empty = await FactoryClient.AddSessionAsync();

        // Act
        var noModel = await FactoryClient.PostAsync(ApiRoutes.Sessions.Cleanup(withWords.Id), null);
        var noWords = await FactoryClient.PostAsync(ApiRoutes.Sessions.Cleanup(empty.Id), null);
        var read = await scenario.ReadAsync(withWords.Id);

        // Assert
        noModel.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        noWords.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        read.CleanupStatus.ShouldBe(nameof(CleanupStatus.NotRun));
    }
}
