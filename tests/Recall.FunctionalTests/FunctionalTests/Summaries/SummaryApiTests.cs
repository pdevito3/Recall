namespace Recall.FunctionalTests.FunctionalTests.Summaries;

using System.Net;
using System.Net.Http.Json;
using Recall.FunctionalTests.FunctionalTests.ModelProviders;
using Recall.FunctionalTests.FunctionalTests.Sessions;
using Recall.Server.Domain.Summaries;
using Recall.Server.Domain.Summaries.Dtos;
using Shouldly;
using TestUtilities;

public class SummaryApiTests : TestBase
{
    private static readonly DateOnly Monday = SummaryScenario.Monday;

    [Test]
    public async Task a_summary_of_another_user_is_not_found()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "my words");
        var mine = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, Monday);
        var (_, other) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(other.Identifier);

        // Act
        var byId = await FactoryClient.GetAsync(ApiRoutes.Summaries.GetRecord(mine.Id!));
        var byPeriod = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);

        // Assert
        byId.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        byPeriod.Status.ShouldBe(nameof(SummaryStatus.Missing));
        byPeriod.Content.ShouldBeEmpty();
    }

    [Test]
    public async Task a_summary_reads_by_its_id()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "my words");
        var mine = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Week, Monday.AddDays(4));

        // Act
        var byId = await FactoryClient.GetFromJsonAsync<SummaryDto>(ApiRoutes.Summaries.GetRecord(mine.Id!));

        // Assert
        byId.ShouldBe(mine);
        byId!.PeriodDate.ShouldBe(Monday);
        byId.PeriodLastDate.ShouldBe(Monday.AddDays(6));
    }

    [Test]
    [Arguments("sum_nothere")]
    [Arguments("ses_notasummary")]
    public async Task an_unknown_id_is_not_found(string id)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var read = await FactoryClient.GetAsync(ApiRoutes.Summaries.GetRecord(id));

        // Assert
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("fortnight", "2026-06-01")]
    [Arguments("0", "2026-06-01")]
    [Arguments("Day", "06/01/2026")]
    [Arguments("Day", "2026-6-1")]
    [Arguments("Day", "9999-12-31")]
    [Arguments("Year", "9999-06-01")]
    [Arguments("Day", "0001-01-01")]
    public async Task a_bad_period_or_date_is_a_validation_problem(string period, string date)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var read = await FactoryClient.GetAsync($"{ApiRoutes.Base}/summaries/{period}/{Uri.EscapeDataString(date)}");
        var open = await FactoryClient.PostAsync($"{ApiRoutes.Base}/summaries/{period}/{Uri.EscapeDataString(date)}/generate", null);

        // Assert
        read.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        open.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    public async Task a_read_starts_no_run()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "my words");

        // Act
        var read = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);

        // Assert
        read.Status.ShouldBe(nameof(SummaryStatus.Missing));
        read.SourceCount.ShouldBe(1);
        (await SummaryScenario.RowsOfAsync(scenario.Member.Id)).ShouldBeEmpty();
    }

    [Test]
    public async Task a_deleted_user_takes_their_summaries_along()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "my words");
        await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, Monday);

        // Act
        await JournalScenario.ExecuteOutOfBandAsync("UPDATE users SET is_deleted = 1 WHERE id = $id", ("$id", scenario.Member.Id));

        // Assert
        (await SummaryScenario.RowsOfAsync(scenario.Member.Id)).ShouldBeEmpty();
    }
}
