namespace Recall.UnitTests.Resources.FileTelemetry;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Recall.Server.Resources;
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
    public void a_tag_that_is_not_a_finite_number_becomes_a_string_and_the_batch_keeps_its_other_spans()
    {
        // Arrange
        var folder = Directory.CreateTempSubdirectory("recall_spans_").FullName;
        var sourceName = $"recall.test.{Guid.NewGuid():N}";
        using var source = new ActivitySource(sourceName);

        try
        {
            // Act
            using (var provider = Sdk.CreateTracerProviderBuilder()
                       .AddSource(sourceName)
                       .AddProcessor(new BatchActivityExportProcessor(new SpanFileExporter(new JsonLinesFile(Path.Combine(folder, "traces-.jsonl"), Limits))))
                       .Build())
            {
                using (var broken = source.StartActivity("broken")!)
                {
                    broken.SetTag("not.a.number", double.NaN);
                    broken.SetTag("too.large", float.PositiveInfinity);
                    broken.SetTag("finite", 1.5);
                }

                using (source.StartActivity("next")!)
                {
                }

                provider.ForceFlush();
            }

            // Assert
            var lines = LinesIn(folder);
            lines.Select(line => line.GetProperty("name").GetString()).ShouldBe(["broken", "next"], ignoreOrder: true);
            var tags = lines.Single(line => line.GetProperty("name").GetString() == "broken").GetProperty("tags");
            tags.GetProperty("not.a.number").GetString().ShouldBe("NaN");
            tags.GetProperty("too.large").GetString().ShouldBe("Infinity");
            tags.GetProperty("finite").GetDouble().ShouldBe(1.5);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void a_gauge_that_is_not_a_finite_number_becomes_a_string_and_the_batch_keeps_its_other_metrics()
    {
        // Arrange
        var folder = Directory.CreateTempSubdirectory("recall_metrics_").FullName;
        var meterName = $"recall.test.{Guid.NewGuid():N}";
        using var meter = new Meter(meterName);
        meter.CreateObservableGauge("recall.broken", () => double.NaN);
        meter.CreateObservableGauge("recall.finite", () => 2.5);

        try
        {
            // Act
            using (var provider = Sdk.CreateMeterProviderBuilder()
                       .AddMeter(meterName)
                       .AddReader(new PeriodicExportingMetricReader(new MetricFileExporter(new JsonLinesFile(Path.Combine(folder, "metrics-.jsonl"), Limits)), 3_600_000))
                       .Build())
            {
                provider.ForceFlush();
            }

            // Assert
            var lines = LinesIn(folder);
            lines.First(line => line.GetProperty("name").GetString() == "recall.broken").GetProperty("value").GetString().ShouldBe("NaN");
            lines.First(line => line.GetProperty("name").GetString() == "recall.finite").GetProperty("value").GetDouble().ShouldBe(2.5);
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

    [Test]
    public void a_tag_that_throws_loses_its_line_and_keeps_the_process_the_batch_and_gives_one_warning()
    {
        // Arrange
        using var console = new StringWriter();
        var folder = Directory.CreateTempSubdirectory("recall_spans_").FullName;
        var path = Path.Combine(folder, "traces-.jsonl");
        var sourceName = $"recall.test.{Guid.NewGuid():N}";
        using var source = new ActivitySource(sourceName);
        var exporter = new CountingFailures(new SpanFileExporter(new JsonLinesFile(path, Limits, new FileSinkFailureNotice(path, TimeProvider.System, console))));
        // TUnit reads each tag when a span ends, so a tag throws only on the thread of the export
        var broken = new ArmedTag(() => throw new InvalidOperationException("broken ToString")) { Armed = false };
        var tooLarge = new ArmedTag(() => new string('x', int.MaxValue)) { Armed = false };

        try
        {
            // Act
            using (var provider = Sdk.CreateTracerProviderBuilder()
                       .AddSource(sourceName)
                       .AddProcessor(new BatchActivityExportProcessor(exporter))
                       .Build())
            {
                foreach (var tag in new[] { broken, tooLarge })
                {
                    using var bad = source.StartActivity("bad")!;
                    bad.SetTag("app.bad", tag);
                }

                using (source.StartActivity("next")!)
                {
                }

                broken.Armed = true;
                tooLarge.Armed = true;
                provider.ForceFlush();
                broken.Armed = false;
                tooLarge.Armed = false;
            }

            // Assert
            exporter.Failures.ShouldBe(1);
            LinesIn(folder).ShouldHaveSingleItem().GetProperty("name").GetString().ShouldBe("next");
            var warning = JsonDocument.Parse(console.ToString().Trim()).RootElement;
            warning.GetProperty("@l").GetString().ShouldBe("Warning");
            warning.GetProperty("Cause").GetString().ShouldBe("broken ToString");
        }
        finally
        {
            broken.Armed = false;
            tooLarge.Armed = false;
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void a_metric_tag_that_throws_loses_its_line_and_keeps_the_other_metrics()
    {
        // Arrange
        using var console = new StringWriter();
        var folder = Directory.CreateTempSubdirectory("recall_metrics_").FullName;
        var path = Path.Combine(folder, "metrics-.jsonl");
        var meterName = $"recall.test.{Guid.NewGuid():N}";
        using var meter = new Meter(meterName);
        var counter = meter.CreateCounter<long>("recall.count");
        var exporter = new MetricFileExporter(new JsonLinesFile(path, Limits, new FileSinkFailureNotice(path, TimeProvider.System, console)));

        try
        {
            // Act
            using (var provider = Sdk.CreateMeterProviderBuilder()
                       .AddMeter(meterName)
                       .AddReader(new PeriodicExportingMetricReader(exporter, 3_600_000))
                       .Build())
            {
                counter.Add(1, new KeyValuePair<string, object?>("app.bad", new ArmedTag(() => throw new InvalidOperationException("broken ToString"))));
                counter.Add(2, new KeyValuePair<string, object?>("app.good", "kept"));
                provider.ForceFlush().ShouldBeFalse();
            }

            // Assert
            var lines = LinesIn(folder);
            lines.ShouldNotBeEmpty();
            lines.ShouldAllBe(line => line.GetProperty("sum").GetInt64() == 2);
            console.ToString().ShouldContain("broken ToString");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public void a_tag_with_a_long_text_of_any_type_is_cut_in_the_line()
    {
        // Arrange
        var folder = Directory.CreateTempSubdirectory("recall_spans_").FullName;
        var sourceName = $"recall.test.{Guid.NewGuid():N}";
        using var source = new ActivitySource(sourceName);

        try
        {
            // Act
            using (Sdk.CreateTracerProviderBuilder()
                       .AddSource(sourceName)
                       .AddProcessor(new SimpleActivityExportProcessor(new SpanFileExporter(new JsonLinesFile(Path.Combine(folder, "traces-.jsonl"), Limits))))
                       .Build())
            {
                using var span = source.StartActivity("span")!;
                span.SetTag("app.long", new Uri($"http://example.test/{new string('x', 60_000)}"));
                span.AddEvent(new ActivityEvent("loaded", tags: new ActivityTagsCollection { ["app.long"] = new string('<', 20_000) }));
            }

            // Assert
            var line = LinesIn(folder).ShouldHaveSingleItem();
            line.GetProperty("tags").GetProperty("app.long").GetString().ShouldNotBeNull().ShouldContain(TelemetryText.CutMarker);
            line.GetProperty("events")[0].GetProperty("tags").GetProperty("app.long").GetString().ShouldNotBeNull().ShouldContain(TelemetryText.CutMarker);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

internal sealed class ArmedTag(Func<string> text)
{
    public bool Armed { get; set; } = true;

    public override string ToString() => Armed ? text() : "not armed";
}

internal sealed class CountingFailures(BaseExporter<Activity> inner) : BaseExporter<Activity>
{
    public int Failures { get; private set; }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        var result = inner.Export(batch);
        if (result == ExportResult.Failure)
            Failures++;
        return result;
    }

    protected override bool OnShutdown(int timeoutMilliseconds) => inner.Shutdown(timeoutMilliseconds);
}
