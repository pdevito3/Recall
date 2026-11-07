namespace Recall.FunctionalTests.FunctionalTests.Summaries;

using System.Net;
using System.Net.Http.Json;
using BackWave.Monitor;
using BackWave.Storage;
using Microsoft.Extensions.DependencyInjection;
using Recall.FakeModelProvider;
using Recall.FunctionalTests.FunctionalTests.ModelProviders;
using Recall.FunctionalTests.FunctionalTests.Sessions;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.Summaries;
using Recall.Server.Domain.Summaries.Jobs;
using Recall.Server.Domain.TimeZones;
using Recall.Server.Resources.Jobs;
using Shouldly;
using TestUtilities;

public class SummaryJobTests : TestBase
{
    private static readonly DateOnly Monday = SummaryScenario.Monday;

    private static IServiceProvider Services => TestingWebApplicationFactory.Instance.Services;

    private static string Words(string marker) => $"a walk by the river {marker}";

    [Test]
    [Arguments(SummaryPeriod.Day)]
    [Arguments(SummaryPeriod.Week)]
    public async Task an_open_period_with_three_sessions_gets_a_ready_summary_that_reads_all_three(SummaryPeriod period)
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "morning coffee");
        await FactoryClient.SessionOnAsync(Monday, "long meeting");
        await FactoryClient.SessionOnAsync(Monday, "evening run");

        var opening = period == SummaryPeriod.Day ? Monday : Monday.AddDays(2);

        // Act
        var opened = await FactoryClient.OpenAsync(period, opening);
        await SummaryScenario.RunAsync(opened.Id!);
        var ready = await FactoryClient.ReadAsync(period, opening);

        // Assert
        opened.Status.ShouldBe(nameof(SummaryStatus.Generating));
        opened.PeriodDate.ShouldBe(Monday);
        ready.Status.ShouldBe(nameof(SummaryStatus.Ready));
        ready.SourceCount.ShouldBe(3);
        ready.Content.ShouldContain("morning coffee");
        ready.Content.ShouldContain("long meeting");
        ready.Content.ShouldContain("evening run");
        ready.GeneratedAt.ShouldNotBeNull();
    }

    [Test]
    public async Task a_month_reads_its_days_and_its_source_count_is_the_days_with_sessions()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "first day");
        await FactoryClient.SessionOnAsync(Monday, "first day again");
        await FactoryClient.SessionOnAsync(Monday.AddDays(9), "tenth day");
        await FactoryClient.SessionOnAsync(Monday.AddDays(29), "last day");

        // Act
        var month = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Month, Monday);
        var rows = await SummaryScenario.RowsOfAsync(scenario.Member.Id);

        // Assert
        month.Status.ShouldBe(nameof(SummaryStatus.Ready));
        month.SourceCount.ShouldBe(3);
        rows.Count(r => r.Period == SummaryPeriod.Day).ShouldBe(3);
        rows.ShouldAllBe(r => r.RunState == SummaryRunState.Ready);
        scenario.Fake.CompletionCalls.ShouldBe(4);
        month.Content.ShouldContain("tenth day");
        (await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday)).SourceCount.ShouldBe(2);
    }

    [Test]
    public async Task a_year_rolls_up_quarters_months_and_days()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(new DateOnly(2026, 2, 3), "winter words");
        await FactoryClient.SessionOnAsync(new DateOnly(2026, 5, 4), "spring words");

        // Act
        var year = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Year, Monday);
        var quarter = await FactoryClient.ReadAsync(SummaryPeriod.Quarter, new DateOnly(2026, 2, 3));

        // Assert
        year.Status.ShouldBe(nameof(SummaryStatus.Ready));
        year.SourceCount.ShouldBe(2);
        year.PeriodDate.ShouldBe(new DateOnly(2026, 1, 1));
        year.PeriodLastDate.ShouldBe(new DateOnly(2026, 12, 31));
        quarter.Status.ShouldBe(nameof(SummaryStatus.Ready));
        year.Content.ShouldContain("winter words");
        year.Content.ShouldContain("spring words");
        (await SummaryScenario.RowsOfAsync(scenario.Member.Id)).Count.ShouldBe(7);
    }

    [Test]
    public async Task a_period_with_no_sources_gets_no_summary_and_no_job()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.AddSessionAsync(SummaryScenario.NoonOf(Monday));

        // Act
        var opened = await FactoryClient.OpenAsync(SummaryPeriod.Week, Monday);

        // Assert
        opened.Status.ShouldBe(nameof(SummaryStatus.Missing));
        opened.Id.ShouldBeNull();
        opened.Content.ShouldBeEmpty();
        (await SummaryScenario.RowsOfAsync(scenario.Member.Id)).ShouldBeEmpty();
        scenario.Fake.CompletionCalls.ShouldBe(0);
    }

    [Test]
    public async Task an_edit_of_a_source_session_makes_the_summary_and_its_roll_ups_stale()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var session = await FactoryClient.SessionOnAsync(Monday, "first words");
        await FactoryClient.OpenAndRunAsync(SummaryPeriod.Month, Monday);

        // Act
        (await FactoryClient.SaveDraftAsync(session.Id, "second words", 1, SummaryScenario.NoonOf(Monday).AddMinutes(5))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert
        var day = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);
        day.Status.ShouldBe(nameof(SummaryStatus.Stale));
        day.Content.ShouldContain("first words");
        (await FactoryClient.ReadAsync(SummaryPeriod.Month, Monday)).Status.ShouldBe(nameof(SummaryStatus.Stale));
    }

    [Test]
    public async Task a_person_rename_that_changes_a_source_makes_the_summary_stale()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var person = (await (await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label = "Sam" })).Content.ReadFromJsonAsync<PersonDto>())!;
        var mention = $$$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"lunch with "},{"type":"mention","attrs":{"personId":"{{{person.Id}}}","label":"Sam"}}]}]}""";
        await FactoryClient.SessionOnAsync(Monday, mention);
        var ready = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, Monday);

        // Act
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.People.GetRecord(person.Id), new { label = "Samuel" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);
        var again = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, Monday);

        // Assert
        ready.Content.ShouldContain("lunch with Sam");
        after.Status.ShouldBe(nameof(SummaryStatus.Stale));
        again.Status.ShouldBe(nameof(SummaryStatus.Ready));
        again.Content.ShouldContain("lunch with Samuel");
    }

    [Test]
    public async Task an_open_of_a_ready_summary_with_no_change_calls_no_provider()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "some words");
        var first = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, Monday);

        // Act
        var second = await FactoryClient.OpenAsync(SummaryPeriod.Day, Monday);
        await SummaryScenario.RefreshAsync(scenario.Member.Id, scenario.Member.TenantId);

        // Assert
        second.Status.ShouldBe(nameof(SummaryStatus.Ready));
        second.GeneratedAt.ShouldBe(first.GeneratedAt);
        scenario.Fake.CompletionCalls.ShouldBe(1);
    }

    [Test]
    [Arguments(FakeMode.Refusal, nameof(SummaryFailure.BadModelOutput))]
    [Arguments(FakeMode.EchoError, nameof(SummaryFailure.ProviderRefused))]
    public async Task a_provider_that_fails_makes_the_summary_failed_and_never_ready(FakeMode mode, string failure)
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "some words");
        scenario.Fake.Behavior.Mode = mode;
        scenario.Fake.Behavior.Status = 400;

        // Act
        var summary = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, Monday);

        // Assert
        summary.Status.ShouldBe(nameof(SummaryStatus.Failed));
        summary.Failure.ShouldBe(failure);
        summary.Content.ShouldBeEmpty();
        summary.GeneratedAt.ShouldBeNull();
    }

    [Test]
    [Arguments(401, 1)]
    [Arguments(400, 3)]
    public async Task a_roll_up_stops_calling_a_provider_that_refuses_its_key_but_not_one_that_refuses_one_request(int status, int calls)
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "first day");
        await FactoryClient.SessionOnAsync(Monday.AddDays(9), "tenth day");
        await FactoryClient.SessionOnAsync(Monday.AddDays(29), "last day");
        scenario.Fake.Behavior.Mode = FakeMode.Status;
        scenario.Fake.Behavior.Status = status;

        // Act
        var month = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Month, Monday);
        var rows = await SummaryScenario.RowsOfAsync(scenario.Member.Id);

        // Assert
        month.Status.ShouldBe(nameof(SummaryStatus.Failed));
        month.Failure.ShouldBe(nameof(SummaryFailure.SourceFailed));
        scenario.Fake.CompletionCalls.ShouldBe(calls);
        rows.Where(r => r.Period == SummaryPeriod.Day).Select(r => r.Failure).ShouldBe([SummaryFailure.ProviderRefused, SummaryFailure.ProviderRefused, SummaryFailure.ProviderRefused]);
    }

    [Test]
    public async Task the_nightly_job_runs_a_failed_summary_after_the_other_work()
    {
        // Arrange: the Day that failed comes first by date, and the provider refuses its key again tonight, so it gets one call.
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var tuesday = Monday.AddDays(1);
        await FactoryClient.SessionOnAsync(Monday, "failed words");
        var fresh = await FactoryClient.SessionOnAsync(tuesday, "fresh words");
        scenario.Fake.Behavior.Mode = FakeMode.Status;
        scenario.Fake.Behavior.Status = 401;
        await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, Monday);
        scenario.Fake.Behavior.Mode = FakeMode.Normal;
        await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, tuesday);
        (await FactoryClient.SaveDraftAsync(fresh.Id, "fresh words, edited", 1, SummaryScenario.NoonOf(tuesday).AddMinutes(5))).StatusCode.ShouldBe(HttpStatusCode.OK);
        scenario.Fake.Behavior.Mode = FakeMode.Status;
        var callsBefore = scenario.Fake.RequestBodies.Count;

        // Act
        await SummaryScenario.RefreshAsync(scenario.Member.Id, scenario.Member.TenantId);

        // Assert
        var tonight = scenario.Fake.RequestBodies.Skip(callsBefore).ToList();
        tonight.Count.ShouldBe(1);
        tonight[0].ShouldContain("fresh words, edited");
        (await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday)).Failure.ShouldBe(nameof(SummaryFailure.ProviderRefused));
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task an_empty_answer_is_a_failed_summary_and_not_a_ready_one_with_no_words(string answer)
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "some words");
        scenario.Fake.Behavior.Reply = answer;

        // Act
        var summary = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, Monday);

        // Assert
        summary.Status.ShouldBe(nameof(SummaryStatus.Failed));
        summary.Content.ShouldBeEmpty();
    }

    [Test]
    public async Task a_provider_that_is_down_makes_the_job_try_again_and_the_last_claim_fails_the_summary()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "some words");
        scenario.Fake.Behavior.Mode = FakeMode.Status;
        scenario.Fake.Behavior.Status = 503;
        var opened = await FactoryClient.OpenAsync(SummaryPeriod.Day, Monday);

        // Act
        var retry = await Should.ThrowAsync<JobSafeTextException>(() => SummaryScenario.RunAsync(opened.Id!));
        var between = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);
        await SummaryScenario.RunAsync(opened.Id!, attempt: SummaryJobs.MaxClaims);
        var last = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);

        // Assert
        retry.Message.ShouldNotContain("some words");
        between.Status.ShouldBe(nameof(SummaryStatus.Generating));
        last.Status.ShouldBe(nameof(SummaryStatus.Failed));
        last.Failure.ShouldBe(nameof(SummaryFailure.ProviderUnavailable));
    }

    [Test]
    public async Task a_claim_after_a_lease_return_ends_the_run_and_a_claim_past_the_limit_fails_it()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "some words");
        await FactoryClient.SessionOnAsync(Monday.AddDays(1), "more words");
        var resumed = await FactoryClient.OpenAsync(SummaryPeriod.Day, Monday);
        var stuck = await FactoryClient.OpenAsync(SummaryPeriod.Day, Monday.AddDays(1));

        // Act
        await SummaryScenario.RunAsync(resumed.Id!, attempt: 2);
        await SummaryScenario.RunAsync(stuck.Id!, attempt: SummaryJobs.MaxClaims + 1);

        // Assert
        (await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday)).Status.ShouldBe(nameof(SummaryStatus.Ready));
        var failed = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday.AddDays(1));
        failed.Status.ShouldBe(nameof(SummaryStatus.Failed));
        failed.Failure.ShouldBe(nameof(SummaryFailure.Interrupted));
    }

    [Test]
    public async Task a_claim_with_no_provider_ends_the_source_runs_that_an_earlier_claim_began()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "some words");
        scenario.Fake.Behavior.Mode = FakeMode.Status;
        scenario.Fake.Behavior.Status = 503;
        var month = await FactoryClient.OpenAsync(SummaryPeriod.Month, Monday);
        var jobId = Guid.NewGuid();
        await Should.ThrowAsync<JobSafeTextException>(() => SummaryScenario.RunAsync(month.Id!, attempt: 1, jobId));
        var dayBetween = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);
        FactoryClient.SignInAsAppUser(scenario.Admin.Identifier);
        (await FactoryClient.DeleteAsync(ApiRoutes.ModelProviders.GetRecord(scenario.Provider.Id))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        FactoryClient.SignInAsAppUser(scenario.Member.Identifier);

        // Act
        await SummaryScenario.RunAsync(month.Id!, attempt: 2, jobId);

        // Assert
        dayBetween.Status.ShouldBe(nameof(SummaryStatus.Generating));
        var monthAfter = await FactoryClient.ReadAsync(SummaryPeriod.Month, Monday);
        monthAfter.Status.ShouldBe(nameof(SummaryStatus.Failed));
        monthAfter.Failure.ShouldBe(nameof(SummaryFailure.NoModelProvider));
        var dayAfter = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);
        dayAfter.Status.ShouldBe(nameof(SummaryStatus.Failed));
        dayAfter.Failure.ShouldBe(nameof(SummaryFailure.Interrupted));
    }

    [Test]
    public async Task a_job_that_ended_with_no_result_leaves_no_summary_generating()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "some words");
        var opened = await FactoryClient.OpenAsync(SummaryPeriod.Day, Monday);
        await JournalScenario.ExecuteOutOfBandAsync(
            "UPDATE backwave_jobs SET state = $state WHERE wire_name = $wire AND CAST(payload AS TEXT) LIKE $summary",
            ("$state", (int)JobState.DeadLettered),
            ("$wire", SummaryJobs.RunWireName),
            ("$summary", $"%{opened.Id}%"));

        // Act
        await ActivatorUtilities.CreateInstance<SummaryJobWatch>(Services).EndRunsOfEndedJobsAsync(CancellationToken.None);
        var summary = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);

        // Assert
        summary.Status.ShouldBe(nameof(SummaryStatus.Failed));
        summary.Failure.ShouldBe(nameof(SummaryFailure.Interrupted));
    }

    [Test]
    public async Task a_job_that_ends_with_no_result_again_after_a_requeue_leaves_no_summary_generating()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "some words");
        var opened = await FactoryClient.OpenAsync(SummaryPeriod.Day, Monday);
        var ofTheJob = ("$summary", (object)$"%{opened.Id}%");
        var wire = ("$wire", (object)SummaryJobs.RunWireName);
        await JournalScenario.ExecuteOutOfBandAsync(
            "UPDATE backwave_jobs SET state = $state WHERE wire_name = $wire AND CAST(payload AS TEXT) LIKE $summary", ("$state", (int)JobState.DeadLettered), wire, ofTheJob);
        var watch = ActivatorUtilities.CreateInstance<SummaryJobWatch>(Services);
        await watch.EndRunsOfEndedJobsAsync(CancellationToken.None);
        var jobs = await Services.GetRequiredService<IJobStore>().ListJobsAsync(new JobQuery { WireName = SummaryJobs.RunWireName, MaxResults = 1000 });
        var jobId = jobs.Single(job => System.Text.Encoding.UTF8.GetString(job.Payload.Span).Contains(opened.Id!, StringComparison.Ordinal)).JobId;

        // An Owner requeues the job, a claim begins a run, and the job ends in the dead letters again.
        await JournalScenario.ExecuteOutOfBandAsync(
            "UPDATE summaries SET run_state = $generating, run_job_id = $job WHERE id = $id",
            ("$generating", nameof(SummaryRunState.Generating)), ("$job", jobId.ToString().ToUpperInvariant()), ("$id", opened.Id!));
        await JournalScenario.ExecuteOutOfBandAsync(
            "UPDATE backwave_jobs SET terminal_at = due_time WHERE wire_name = $wire AND CAST(payload AS TEXT) LIKE $summary", wire, ofTheJob);

        // Act
        await watch.EndRunsOfEndedJobsAsync(CancellationToken.None);
        var summary = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);

        // Assert
        summary.Status.ShouldBe(nameof(SummaryStatus.Failed));
        summary.Failure.ShouldBe(nameof(SummaryFailure.Interrupted));
    }

    [Test]
    public async Task a_refresh_job_that_ended_with_no_result_fails_the_runs_that_it_began()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "some words");
        var jobId = Guid.NewGuid();
        var opened = await FactoryClient.OpenAsync(SummaryPeriod.Day, Monday);
        await JournalScenario.ExecuteOutOfBandAsync("UPDATE summaries SET run_job_id = $job WHERE id = $id", ("$job", jobId.ToString().ToUpperInvariant()), ("$id", opened.Id!));

        // Act
        await RefreshWithAsync(scenario, jobId, attempt: SummaryJobs.MaxClaims + 1);
        var summary = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);

        // Assert
        summary.Status.ShouldBe(nameof(SummaryStatus.Failed));
        summary.Failure.ShouldBe(nameof(SummaryFailure.Interrupted));
    }

    [Test]
    public async Task the_nightly_job_regenerates_only_the_stale_summaries()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        var changed = await FactoryClient.SessionOnAsync(Monday, "monday words");
        await FactoryClient.SessionOnAsync(Monday.AddDays(1), "tuesday words");
        await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, Monday);
        var kept = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, Monday.AddDays(1));
        await FactoryClient.SaveDraftAsync(changed.Id, "new monday words", 1, SummaryScenario.NoonOf(Monday).AddMinutes(5));
        var callsBefore = scenario.Fake.CompletionCalls;

        // Act
        await SummaryScenario.RefreshAsync(scenario.Member.Id, scenario.Member.TenantId);

        // Assert
        scenario.Fake.CompletionCalls.ShouldBe(callsBefore + 1);
        var monday = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);
        monday.Status.ShouldBe(nameof(SummaryStatus.Ready));
        monday.Content.ShouldContain("new monday words");
        (await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday.AddDays(1))).GeneratedAt.ShouldBe(kept.GeneratedAt);
    }

    [Test]
    public async Task the_nightly_job_with_no_provider_changes_nothing()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        await FactoryClient.SessionOnAsync(Monday, "some words");
        var failed = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, Monday);

        // Act
        await SummaryScenario.RefreshAsync(member.Id, member.TenantId);
        var after = await FactoryClient.ReadAsync(SummaryPeriod.Day, Monday);

        // Assert
        failed.Status.ShouldBe(nameof(SummaryStatus.Failed));
        failed.Failure.ShouldBe(nameof(SummaryFailure.NoModelProvider));
        after.ShouldBe(failed);
    }

    [Test]
    [Arguments("Pacific/Auckland")]
    [Arguments("America/Los_Angeles")]
    public async Task the_nightly_job_makes_the_day_that_ended_in_the_zone_of_the_user(string zone)
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await SetZoneAsync(zone);
        var today = ZonedDate.TodayAt(TimeZoneId.Of(zone), DateTimeOffset.UtcNow);
        var yesterday = today.Date.AddDays(-1);
        await FactoryClient.SessionAtAsync(today.Start.AddMinutes(-30), "late words");

        // Act
        await SummaryScenario.RefreshAsync(scenario.Member.Id, scenario.Member.TenantId);
        var day = await FactoryClient.ReadAsync(SummaryPeriod.Day, yesterday);

        // Assert
        day.Status.ShouldBe(nameof(SummaryStatus.Ready));
        day.Content.ShouldContain("late words");
        (await FactoryClient.ReadAsync(SummaryPeriod.Day, yesterday.AddDays(1))).Status.ShouldBe(nameof(SummaryStatus.Missing));
    }

    [Test]
    public async Task the_journaling_day_of_a_source_comes_from_the_zone_of_the_user()
    {
        // Arrange: 22:00 UTC on Monday is 10:00 on Tuesday in Auckland
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await SetZoneAsync("Pacific/Auckland");
        await FactoryClient.SessionAtAsync(new DateTimeOffset(Monday.ToDateTime(new TimeOnly(22, 0)), TimeSpan.Zero), "auckland words");

        // Act
        var monday = await FactoryClient.OpenAsync(SummaryPeriod.Day, Monday);
        var tuesday = await FactoryClient.OpenAndRunAsync(SummaryPeriod.Day, Monday.AddDays(1));

        // Assert
        monday.Status.ShouldBe(nameof(SummaryStatus.Missing));
        tuesday.Status.ShouldBe(nameof(SummaryStatus.Ready));
        tuesday.Content.ShouldContain("auckland words");
    }

    [Test]
    public async Task the_nightly_schedule_has_no_overlap_and_follows_the_zone_of_the_user()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "some words");

        // Act
        await FactoryClient.OpenAsync(SummaryPeriod.Day, Monday);
        var first = await ScheduleOfAsync(scenario.Member.Id);
        await SetZoneAsync("Asia/Tokyo");
        var moved = await ScheduleOfAsync(scenario.Member.Id);

        // Assert
        first.NoOverlap.ShouldBeTrue();
        first.TimeZoneId.ShouldBe("UTC");
        first.WireName.ShouldBe(SummaryJobs.RefreshWireName);
        first.Cron.ShouldBe(SummarySchedules.Nightly.Canonical);
        moved.TimeZoneId.ShouldBe("Asia/Tokyo");
        moved.NoOverlap.ShouldBeTrue();
    }

    [Test]
    public async Task each_summary_job_of_a_tenant_goes_to_the_queue_of_its_tenant()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, "some words");

        // Act
        var opened = await FactoryClient.OpenAsync(SummaryPeriod.Day, Monday);
        var monitor = Services.GetRequiredService<BackWaveMonitor>();
        var runs = await monitor.ListJobsAsync(new JobQuery { WireName = SummaryJobs.RunWireName, MaxResults = 1000 });
        var queues = new List<string>();
        foreach (var job in runs)
        {
            if ((await monitor.GetJobPayloadAsync(job.JobId))?.Text.Contains(opened.Id!, StringComparison.Ordinal) == true)
                queues.Add(job.Queue);
        }
        var schedule = await ScheduleOfAsync(scenario.Member.Id);

        // Assert
        var queue = SummaryQueues.Of(scenario.Member.TenantId);
        SummaryQueues.All.ShouldContain(queue);
        queues.ShouldHaveSingleItem().ShouldBe(queue);
        schedule.Queue.ShouldBe(queue);
    }

    [Test]
    public async Task a_job_payload_holds_no_words_and_no_key()
    {
        // Arrange
        var marker = $"PRIVATEWORDS{Guid.NewGuid():N}";
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await FactoryClient.SessionOnAsync(Monday, Words(marker));

        // Act
        var opened = await FactoryClient.OpenAsync(SummaryPeriod.Day, Monday);
        var monitor = Services.GetRequiredService<BackWaveMonitor>();
        var jobs = await monitor.ListJobsAsync(new JobQuery { WireName = SummaryJobs.RunWireName, MaxResults = 1000 });
        var payloads = new List<string>();
        foreach (var job in jobs)
            payloads.Add((await monitor.GetJobPayloadAsync(job.JobId))?.Text ?? string.Empty);
        var payload = payloads.Where(p => p.Contains(opened.Id!, StringComparison.Ordinal)).ShouldHaveSingleItem();
        await SummaryScenario.RunAsync(opened.Id!);

        // Assert
        payload.ShouldNotContain(marker);
        payload.ShouldNotContain(scenario.ApiKey);
        (await CleanupScenario.DumpOfTableAsync("backwave_jobs")).ShouldNotContain(marker);
    }

    private async Task SetZoneAsync(string zone)
    {
        var current = (await FactoryClient.GetFromJsonAsync<Recall.Server.Domain.UserSettings.Dtos.UserSettingDto>(ApiRoutes.UserSettings.Root))!;
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZone = zone, locationCaptureEnabled = false, requirePeopleProposalApproval = true, version = current.Version });
        change.StatusCode.ShouldBe(HttpStatusCode.OK, await change.Content.ReadAsStringAsync());
    }

    private static async Task<ScheduleRecord> ScheduleOfAsync(string userId)
        => (await Services.GetRequiredService<IJobStore>().ListSchedulesAsync()).Select(s => s.Schedule).Single(s => s.ScheduleId == SummarySchedules.IdOf(userId));

    private static Task RefreshWithAsync(CleanupScenario scenario, Guid jobId, int attempt)
        => SummaryScenario.RefreshAsync(scenario.Member.Id, scenario.Member.TenantId, jobId, attempt);
}
