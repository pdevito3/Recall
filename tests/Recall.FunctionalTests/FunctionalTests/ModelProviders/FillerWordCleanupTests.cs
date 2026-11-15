namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.UserSettings.Dtos;
using Shouldly;
using TestUtilities;

public class FillerWordCleanupTests : TestBase
{
    private const string Words = "Um, I went to the store, uh, today";

    [Test]
    [Property("Rule", "SES-51")]
    [Property("Rule", "SES-20")]
    public async Task with_the_removal_on_the_provider_gets_the_rule_to_remove_speech_noise_and_raw_stays_as_it_was_said()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var started = await scenario.SessionWithRunningCleanupAsync(Words);

        // Act
        await scenario.RunJobAsync(started.Id);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        var prompt = scenario.Fake.RequestBodies.ShouldHaveSingleItem();
        prompt.ShouldContain("Speech noise is not an idea");
        prompt.ShouldNotContain("keep the way they spoke");
        session.RawText.ShouldBe(started.RawText);
        session.RawText.ShouldContain(Words);
    }

    [Test]
    [Property("Rule", "SES-50")]
    [Property("Rule", "SES-51")]
    [Property("Rule", "SES-20")]
    public async Task with_the_removal_off_the_provider_gets_the_rule_to_keep_speech_noise_and_raw_stays_as_it_was_said()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var settings = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new UserSettingChangeDto { TimeZoneMode = "FollowDevice", LocationCaptureEnabled = false, RequirePeopleProposalApproval = true, RemoveFillerWords = false, Version = 0 });
        settings.StatusCode.ShouldBe(HttpStatusCode.OK, await settings.Content.ReadAsStringAsync());
        var started = await scenario.SessionWithRunningCleanupAsync(Words);

        // Act
        await scenario.RunJobAsync(started.Id);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        var prompt = scenario.Fake.RequestBodies.ShouldHaveSingleItem();
        prompt.ShouldContain("keep the way they spoke");
        prompt.ShouldNotContain("Speech noise is not an idea");
        session.RawText.ShouldBe(started.RawText);
        session.RawText.ShouldContain(Words);
    }
}
