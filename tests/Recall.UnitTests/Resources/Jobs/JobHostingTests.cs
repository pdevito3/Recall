namespace Recall.UnitTests.Resources.Jobs;

using System.Diagnostics;
using Microsoft.AspNetCore.Http;
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
