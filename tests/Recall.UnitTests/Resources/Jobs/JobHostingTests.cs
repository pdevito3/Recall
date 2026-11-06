namespace Recall.UnitTests.Resources.Jobs;

using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
