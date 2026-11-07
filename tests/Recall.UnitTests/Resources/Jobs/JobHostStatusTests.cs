namespace Recall.UnitTests.Resources.Jobs;

using System.Runtime.CompilerServices;
using BackWave.Hosting;
using Recall.Server.Resources.Jobs;
using Shouldly;

public class JobHostStatusTests
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ReportHalted")]
    private static extern void ReportHalted(BackWaveHealth health, string workerGroup, string pump, int groupPumpCount, Exception exception);

    [Test]
    [Property("Rule", "REC-14")]
    [Arguments(JobHosting.TranscriptionQueue, true)]
    [Arguments(JobHosting.SystemQueue, false)]
    public async Task only_the_group_that_makes_text_stops_the_transcription(string group, bool stopped)
    {
        // Arrange
        var health = new BackWaveHealth();
        var status = new JobHostStatus(health);

        // Act
        ReportHalted(health, group, "pump", 1, new InvalidOperationException());

        // Assert
        status.TranscriptionIsStopped.ShouldBe(stopped);
        status.StoppedCause().ShouldNotBeNull();
    }

    [Test]
    [Property("Rule", "REC-14")]
    public async Task a_job_store_that_never_started_stops_the_transcription()
    {
        // Arrange
        var status = new JobHostStatus(new BackWaveHealth());
        var before = status.TranscriptionIsStopped;

        // Act
        status.ReportStartFault(new InvalidOperationException());

        // Assert
        before.ShouldBeFalse();
        status.TranscriptionIsStopped.ShouldBeTrue();
    }
}
