namespace Recall.UnitTests.Resources.Jobs;

using System.Diagnostics;
using BackWave.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Recall.Server.Resources.Entrypoints.Jobs;
using Recall.Server.Resources.Jobs;
using Shouldly;

public class JobHostingTests
{
    [Test]
    [Arguments("/backwave", true)]
    [Arguments("/backwave/jobs", true)]
    [Arguments("/backwave-other", false)]
    [Arguments("/api/v1/users", false)]
    [Arguments("/", false)]
    public void only_the_paths_of_the_dashboard_skip_the_csrf_header(string path, bool expected)
    {
        // Act
        var serves = JobDashboard.Serves(new PathString(path));

        // Assert
        serves.ShouldBe(expected);
    }

    [Test]
    [Arguments("Production", false)]
    [Arguments("Staging", false)]
    [Arguments("Development", true)]
    [Arguments("FunctionalTesting", true)]
    [Arguments("IntegrationTesting", true)]
    public void the_settings_that_fail_the_health_job_work_only_in_development_and_in_a_test(string environment, bool honored)
    {
        // Arrange
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration["Jobs:HealthJob:FailOnPurpose"] = "true";
        builder.Configuration["Jobs:HealthJob:HoldSeconds"] = "9";
        builder.AddJobs();
        using var services = builder.Services.BuildServiceProvider();

        // Act
        var settings = services.GetRequiredService<IOptions<JobOptions>>().Value.HealthJob;

        // Assert
        settings.FailOnPurpose.ShouldBe(honored);
        settings.HoldSeconds.ShouldBe(honored ? 9 : 0);
    }

    [Test]
    [Arguments("Production", true)]
    [Arguments("Staging", true)]
    [Arguments("Development", false)]
    [Arguments("FunctionalTesting", false)]
    public void the_setting_that_runs_no_worker_works_only_in_development_and_in_a_test(string environment, bool runsWorkers)
    {
        // Arrange
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration["Jobs:RunWorkers"] = "false";

        // Act
        builder.AddJobs();

        // Assert
        builder.Services.Any(service => service.ImplementationType == typeof(JobHostWatch)).ShouldBe(runsWorkers);
    }

    [Test]
    public void the_publish_output_has_the_license_and_the_required_notice()
    {
        // Arrange
        var license = Path.Combine(AppContext.BaseDirectory, "ThirdPartyNotices", "BackWave", "LICENSE.md");

        // Act
        var text = File.ReadAllText(license);

        // Assert
        text.ShouldContain("PolyForm Shield License 1.0.0");
        text.ShouldContain("Required Notice: Copyright © DeVito Digital Solutions LLC (https://backwave.app)");
    }

    [Test]
    [Property("Rule", "REC-13")]
    public void the_transcription_group_runs_one_job_at_a_time_on_its_own_queue()
    {
        // Act
        var group = JobHosting.TranscriptionWorkers(new JobOptions());

        // Assert
        group.PoolSize.ShouldBe(1);
        group.Policy.ShouldBeOfType<DispatchPolicy.Strict>().Queues.ShouldBe([JobHosting.TranscriptionQueue]);
    }

    [Test]
    public void each_cleanup_limit_is_written_before_a_worker_group_can_claim()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });

        // Act
        builder.AddJobs();

        // Assert
        var hosted = builder.Services.Where(service => service.ServiceType == typeof(IHostedService)).ToList();
        var limits = hosted.FindIndex(service => service.ImplementationType == typeof(CleanupQueueLimits));
        limits.ShouldBeGreaterThanOrEqualTo(0);
        hosted.Take(limits).ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-44")]
    public void a_cleanup_has_its_own_workers_and_a_system_job_never_waits_behind_one()
    {
        // Act
        var cleanups = JobHosting.CleanupWorkers(new JobOptions());

        // Assert
        cleanups.PoolSize.ShouldBe(CleanupQueues.Count * CleanupQueues.JobsAtOnceInAQueue);
        cleanups.Policy.ShouldBeOfType<DispatchPolicy.Weighted>().Queues.ShouldBe(CleanupQueues.All);
        CleanupQueues.All.ShouldNotContain(JobHosting.SystemQueue);
        JobHosting.SystemWorkers(new JobOptions()).Policy.ShouldBeOfType<DispatchPolicy.Strict>().Queues.ShouldBe([JobHosting.SystemQueue]);
    }

    [Test]
    public void a_tenant_takes_a_queue_that_no_other_tenant_uses_and_keeps_it_while_its_cleanups_run()
    {
        // Arrange
        var sameHash = Enumerable.Range(0, 800).Select(i => $"tenant-{i}").Where(t => CleanupQueues.Of(t) == CleanupQueues.Of("tenant-a")).First();
        var hung = Enumerable.Repeat((CleanupQueues.Of("tenant-a"), "tenant-a"), 4).ToList();

        // Act
        var second = CleanupQueues.Pick(sameHash, hung);
        var again = CleanupQueues.Pick("tenant-a", [.. hung, (second, sameHash)]);
        var alone = CleanupQueues.Pick(sameHash, []);
        var allUsed = CleanupQueues.All.Select((queue, i) => (queue, $"other-{i}")).ToList();

        // Assert
        second.ShouldNotBe(CleanupQueues.Of("tenant-a"));
        again.ShouldBe(CleanupQueues.Of("tenant-a"));
        alone.ShouldBe(CleanupQueues.Of(sameHash));
        CleanupQueues.Pick(sameHash, [.. hung, (second, sameHash)]).ShouldBe(second);
        CleanupQueues.Pick(sameHash, allUsed).ShouldBe(CleanupQueues.Of(sameHash));
    }

    [Test]
    public void a_tenant_keeps_its_queue_in_each_process_and_tenants_spread_over_the_queues()
    {
        // Act
        var queues = Enumerable.Range(0, 800).Select(i => CleanupQueues.Of($"tenant-{i}")).ToList();

        // Assert
        CleanupQueues.Of("tenant-0").ShouldBe("cleanup-0");
        CleanupQueues.Of("tenant-1").ShouldBe("cleanup-3");
        queues.Distinct().Order().ShouldBe(CleanupQueues.All);
        queues.GroupBy(queue => queue).Min(group => group.Count()).ShouldBeGreaterThan(60);
    }

    [Test]
    [Arguments("ffmpeg", "NOTICE.md", "https://ffmpeg.org/releases/ffmpeg-7.1.3.tar.xz")]
    [Arguments("ffmpeg", "COPYING.LGPLv2.1", "GNU LESSER GENERAL PUBLIC LICENSE")]
    [Arguments("sherpa-onnx", "LICENSE", "Apache License")]
    [Arguments("onnxruntime", "LICENSE", "MIT License")]
    [Arguments("parakeet-tdt-0.6b-v2", "NOTICE.md", "Creative Commons Attribution 4.0")]
    public void the_publish_output_has_the_licenses_of_the_speech_parts(string part, string file, string expected)
    {
        // Arrange
        var path = Path.Combine(AppContext.BaseDirectory, "ThirdPartyNotices", part, file);

        // Act
        var text = File.ReadAllText(path);

        // Assert
        text.ShouldContain(expected);
    }

    [Test]
    [Arguments("BackWave", 0, false)]
    [Arguments("BackWave", 1, true)]
    [Arguments("Other", 0, true)]
    public void a_span_of_an_empty_claim_goes_to_no_exporter(string sourceName, int claimedCount, bool recorded)
    {
        // Arrange
        using var source = new ActivitySource(sourceName);
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var span = source.StartActivity("receive");
        span.ShouldNotBeNull();
        span.SetTag("backwave.claimed_count", claimedCount);
        using var filter = new EmptyClaimSpanFilter();

        // Act
        filter.OnEnd(span);

        // Assert
        span.Recorded.ShouldBe(recorded);
    }
}
