namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.Sessions.Content;
using Recall.Server.Domain.Sessions.Dtos;
using Shouldly;
using TestUtilities;

/// <summary>"I do not love Ann" and "I do ~~not~~ love Ann" say opposite things, so each plain text of the server keeps the strike.</summary>
public class StrikeInPlainTextTests : TestBase
{
    private const string Struck = "I do ~~not~~ love Ann.";
    private static readonly string[] Ann = ["Ann"];

    private async Task<SessionDto> PostAsync(string route, object body)
    {
        var answer = await FactoryClient.PostAsJsonAsync(route, body);
        answer.StatusCode.ShouldBe(HttpStatusCode.OK, await answer.Content.ReadAsStringAsync());
        return (await answer.Content.ReadFromJsonAsync<SessionDto>()).ShouldNotBeNull();
    }

    [Test]
    [Property("Rule", "SES-45")]
    public async Task the_context_of_a_proposal_keeps_a_struck_word_and_the_tag_keeps_the_strike()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await scenario.SessionWithWordsAsync("raw words about Ann");
        var stored = await PostAsync(ApiRoutes.Sessions.CleanupResult(session.Id), new { cleanedMarkdown = Struck, peopleProposal = Ann, baseRawRevision = 1 });

        // Act
        var tagged = await PostAsync(ApiRoutes.Sessions.RespondToPersonProposal(session.Id), new { label = "Ann", approve = true, createNew = false });

        // Assert
        stored.PeopleProposals.ShouldHaveSingleItem().Contexts.ShouldBe([Struck]);
        PersonMentions.PersonIdsIn(tagged.CleanedText).Count.ShouldBe(1);
        ProseMirrorToPlainText.Render(tagged.CleanedText).ShouldBe(Struck);
    }

    [Test]
    [Property("Rule", "SES-45")]
    public async Task a_struck_word_of_raw_reaches_the_model_provider_as_struck()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var started = await scenario.SessionWithRunningCleanupAsync(MarkdownToProseMirror.ConvertToJson(Struck));

        // Act
        await scenario.RunJobAsync(started.Id);

        // Assert
        var body = scenario.Fake.RequestBodies.ShouldHaveSingleItem();
        body.ShouldContain(Struck);
        body.ShouldContain("Words between ~~ and ~~ are struck");
    }
}
