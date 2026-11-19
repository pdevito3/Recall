namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Net;
using System.Net.Http.Json;
using Shouldly;
using TestUtilities;

public class KnownWordCleanupTests : TestBase
{
    [Test]
    [Property("Rule", "SES-54")]
    [Property("Rule", "SES-20")]
    public async Task a_known_word_with_no_mishearings_reaches_the_provider_and_raw_stays_as_it_was_said()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var correction = await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, new { canonicalTerm = "Radancy", mishearings = Array.Empty<string>(), hardReplace = false });
        correction.StatusCode.ShouldBe(HttpStatusCode.Created, await correction.Content.ReadAsStringAsync());
        var started = await scenario.SessionWithRunningCleanupAsync("I started at Radiancy today");

        // Act
        await scenario.RunJobAsync(started.Id);
        var session = await scenario.ReadAsync(started.Id);

        // Assert
        var prompt = scenario.Fake.RequestBodies.ShouldHaveSingleItem();
        prompt.ShouldContain("The person uses these known words.");
        prompt.ShouldContain("Radancy");
        session.RawText.ShouldBe(started.RawText);
        session.RawText.ShouldContain("I started at Radiancy today");
    }
}
