namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.UserSettings.Dtos;
using Shouldly;
using TestUtilities;

public class NumberFormatCleanupTests : TestBase
{
    private const string Words = "I paid twenty dollars at three thirty pm";

    [Test]
    [Property("Rule", "SES-53")]
    [Property("Rule", "SES-20")]
    public async Task with_the_number_format_on_the_provider_gets_the_rule_to_write_numbers_in_written_form_and_raw_stays_as_it_was_said()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var started = await scenario.SessionWithRunningCleanupAsync(Words);

        // Act
        await scenario.RunJobAsync(started.Id);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        var prompt = scenario.Fake.RequestBodies.ShouldHaveSingleItem();
        prompt.ShouldContain("in its usual written form");
        prompt.ShouldNotContain("keep each number as they said it");
        session.RawText.ShouldBe(started.RawText);
        session.RawText.ShouldContain(Words);
        session.CleanedText.ShouldContain($"{Words} (cleaned)");
    }

    [Test]
    [Property("Rule", "SES-52")]
    [Property("Rule", "SES-53")]
    [Property("Rule", "SES-20")]
    public async Task with_the_number_format_off_the_provider_gets_the_rule_to_keep_each_number_as_said_and_raw_stays_as_it_was_said()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var settings = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new UserSettingChangeDto { TimeZoneMode = "FollowDevice", LocationCaptureEnabled = false, RequirePeopleProposalApproval = true, WriteNumbersAsDigits = false, Version = 0 });
        settings.StatusCode.ShouldBe(HttpStatusCode.OK, await settings.Content.ReadAsStringAsync());
        var started = await scenario.SessionWithRunningCleanupAsync(Words);

        // Act
        await scenario.RunJobAsync(started.Id);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        var prompt = scenario.Fake.RequestBodies.ShouldHaveSingleItem();
        prompt.ShouldContain("keep each number as they said it");
        prompt.ShouldNotContain("in its usual written form");
        session.RawText.ShouldBe(started.RawText);
        session.RawText.ShouldContain(Words);
    }
}
