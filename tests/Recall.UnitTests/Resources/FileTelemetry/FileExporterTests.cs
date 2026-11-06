namespace Recall.UnitTests.Resources.FileTelemetry;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Recall.Server.Resources.FileTelemetry;
using Shouldly;

public class FileExporterTests
{
    private static readonly RollingFileLimits Limits = new() { FileSizeBytes = 1_048_576, FilesKept = 3, DaysKept = 14 };

    private static JsonElement[] LinesIn(string folder) =>
    [
        .. Directory.GetFiles(folder)
            .SelectMany(File.ReadLines)
            .Select(line => JsonDocument.Parse(line).RootElement),
    ];

    [Test]
    public void a_span_writes_one_json_line_with_its_ids_tags_events_and_status()
    {
        // Arrange
        var folder = Directory.CreateTempSubdirectory("recall_spans_").FullName;
        var sourceName = $"recall.test.{Guid.NewGuid():N}";
        using var source = new ActivitySource(sourceName);
        string traceId;
        string parentId;
        string childId;

        try
        {
            // Act
            using (Sdk.CreateTracerProviderBuilder()
                       .AddSource(sourceName)
                       .AddProcessor(new SimpleActivityExportProcessor(new SpanFileExporter(new JsonLinesFile(Path.Combine(folder, "traces-.jsonl"), Limits))))
                       .Build())
            {
                using var parent = source.StartActivity("parent")!;
                using (var child = source.StartActivity("child")!)
                {
                    child.SetTag("recall.count", 3);
                    child.SetTag("recall.name", "a \"quoted\" value");
                    child.AddEvent(new ActivityEvent("loaded", tags: new ActivityTagsCollection { ["rows"] = 2 }));
                    child.SetStatus(ActivityStatusCode.Error, "broken");
                    childId = child.SpanId.ToHexString();
                }

                traceId = parent.TraceId.ToHexString();
                parentId = parent.SpanId.ToHexString();
            }

            // Assert
            var lines = LinesIn(folder);
            lines.Length.ShouldBe(2);
            var line = lines.Single(span => span.GetProperty("name").GetString() == "child");
            line.GetProperty("traceId").GetString().ShouldBe(traceId);
            line.GetProperty("spanId").GetString().ShouldBe(childId);
            line.GetProperty("parentSpanId").GetString().ShouldBe(parentId);
            line.GetProperty("status").GetString().ShouldBe("Error");
            line.GetProperty("statusDescription").GetString().ShouldBe("broken");
            line.GetProperty("source").GetString().ShouldBe(sourceName);
            line.GetProperty("tags").GetProperty("recall.count").GetInt64().ShouldBe(3);
            line.GetProperty("tags").GetProperty("recall.name").GetString().ShouldBe("a \"quoted\" value");
            var loaded = line.GetProperty("events").EnumerateArray().ShouldHaveSingleItem();
            loaded.GetProperty("name").GetString().ShouldBe("loaded");
            loaded.GetProperty("tags").GetProperty("rows").GetInt64().ShouldBe(2);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void a_counter_and_a_histogram_write_their_values_in_lines_of_json()
    {
        // Arrange
        var folder = Directory.CreateTempSubdirectory("recall_metrics_").FullName;
        var meterName = $"recall.test.{Guid.NewGuid():N}";
        using var meter = new Meter(meterName);
        var counter = meter.CreateCounter<long>("recall.count", unit: "{thing}");
        var histogram = meter.CreateHistogram<double>("recall.time");

        try
        {
            // Act
            using (var provider = Sdk.CreateMeterProviderBuilder()
                       .AddMeter(meterName)
                       .AddReader(new PeriodicExportingMetricReader(new MetricFileExporter(new JsonLinesFile(Path.Combine(folder, "metrics-.jsonl"), Limits)), 3_600_000))
                       .Build())
            {
                counter.Add(4, new KeyValuePair<string, object?>("kind", "a"));
                histogram.Record(7);
                histogram.Record(9);
                provider.ForceFlush();
            }

            // Assert
            var lines = LinesIn(folder);
            var sum = lines.First(line => line.GetProperty("name").GetString() == "recall.count");
            sum.GetProperty("sum").GetInt64().ShouldBe(4);
            sum.GetProperty("unit").GetString().ShouldBe("{thing}");
            sum.GetProperty("tags").GetProperty("kind").GetString().ShouldBe("a");
            var time = lines.First(line => line.GetProperty("name").GetString() == "recall.time");
            time.GetProperty("count").GetInt64().ShouldBe(2);
            time.GetProperty("sum").GetDouble().ShouldBe(16);
            time.GetProperty("buckets").GetProperty("10").GetInt64().ShouldBe(2);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
