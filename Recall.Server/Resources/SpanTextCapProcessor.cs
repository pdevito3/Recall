namespace Recall.Server.Resources;

using System.Diagnostics;
using OpenTelemetry;

/// <summary>
/// Add it before each exporter. It cuts a span when the span ends, so each exporter gets the cut span, and a batch queue
/// does not keep the long strings in memory. This runs on the thread of the request, so it must not throw.
/// </summary>
public sealed class SpanTextCapProcessor : BaseProcessor<Activity>
{
    private const string ExceptionMessageTag = "exception.message";
    private const string ExceptionStackTraceTag = "exception.stacktrace";

    // An event cannot change after it is on the span. This listener gives AddException its text, and each instrumentation uses AddException.
    private readonly ActivityListener _exceptions = new()
    {
        ShouldListenTo = _ => true,
        ExceptionRecorder = CapTheExceptionText,
    };

    public SpanTextCapProcessor() => ActivitySource.AddActivityListener(_exceptions);

    public override void OnEnd(Activity data)
    {
        try
        {
            data.DisplayName = TelemetryText.Cap(data.DisplayName);
            if (data.StatusDescription is { Length: > TelemetryText.MaxLength } description)
                data.SetStatus(data.Status, TelemetryText.Cap(description));

            foreach (var (key, value) in data.TagObjects.Where(tag => tag.Value is string { Length: > TelemetryText.MaxLength }).ToList())
                data.SetTag(key, TelemetryText.Cap(value));
        }
        catch (Exception)
        {
            // The span stays as it is. The exporter holds the limit of its own, and the request goes on.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _exceptions.Dispose();

        base.Dispose(disposing);
    }

    private static void CapTheExceptionText(Activity activity, Exception exception, ref TagList tags)
    {
        AddWhenCut(ref tags, ExceptionMessageTag, exception.Message);
        AddWhenCut(ref tags, ExceptionStackTraceTag, exception.ToString());
    }

    private static void AddWhenCut(ref TagList tags, string key, string text)
    {
        if (text.Length <= TelemetryText.MaxLength)
            return;

        foreach (var tag in tags)
        {
            if (tag.Key == key)
                return;
        }

        tags.Add(key, TelemetryText.Cap(text));
    }
}
