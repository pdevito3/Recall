namespace Recall.Server.Resources.Jobs;

using System.Diagnostics;
using OpenTelemetry;

/// <summary>
/// BackWave starts a "receive" span for each poll. An idle worker would write a span to the telemetry files at each
/// poll and keep the disk of a NAS awake, so a span that claimed no job goes to no exporter.
/// </summary>
public sealed class EmptyClaimSpanFilter : BaseProcessor<Activity>
{
    private const string ClaimedCountTag = "backwave.claimed_count";

    public override void OnEnd(Activity data)
    {
        if (data.Source.Name == "BackWave" && data.GetTagItem(ClaimedCountTag) is 0)
            data.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
    }
}
