namespace Recall.Server.Resources.FileTelemetry;

using System.Diagnostics;
using System.Globalization;
using OpenTelemetry;

public sealed class SpanFileExporter(JsonLinesFile file) : BaseExporter<Activity>
{
    public override ExportResult Export(in Batch<Activity> batch)
    {
        var result = ExportResult.Success;
        foreach (var span in batch)
        {
            try
            {
                file.Write(LineOf(span));
            }
            catch (Exception exception)
            {
                file.ReportLostLine(exception);
                result = ExportResult.Failure;
            }
        }

        return result;
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        file.Dispose();
        return true;
    }

    public static string LineOf(Activity span) => JsonLine.Of(json =>
    {
        json.WriteString("time", span.StartTimeUtc);
        json.WriteString("traceId", span.TraceId.ToHexString());
        json.WriteString("spanId", span.SpanId.ToHexString());
        if (span.ParentSpanId != default)
            json.WriteString("parentSpanId", span.ParentSpanId.ToHexString());
        json.WriteString("name", span.DisplayName);
        json.WriteString("kind", span.Kind.ToString());
        json.WriteString("source", span.Source.Name);
        json.WriteNumber("durationMs", span.Duration.TotalMilliseconds);
        json.WriteString("status", span.Status.ToString());
        if (span.StatusDescription is { Length: > 0 } description)
            json.WriteString("statusDescription", description);
        JsonLine.WriteTags(json, "tags", span.TagObjects);

        if (span.Events.Any())
        {
            json.WriteStartArray("events");
            foreach (var spanEvent in span.Events)
            {
                json.WriteStartObject();
                json.WriteString("time", spanEvent.Timestamp);
                json.WriteString("name", spanEvent.Name);
                JsonLine.WriteTags(json, "tags", spanEvent.Tags);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        if (span.Links.Any())
        {
            json.WriteStartArray("links");
            foreach (var link in span.Links)
                json.WriteStringValue(string.Create(CultureInfo.InvariantCulture, $"{link.Context.TraceId}-{link.Context.SpanId}"));
            json.WriteEndArray();
        }
    });
}
