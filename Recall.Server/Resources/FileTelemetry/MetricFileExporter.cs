namespace Recall.Server.Resources.FileTelemetry;

using System.Globalization;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Metrics;

public sealed class MetricFileExporter(JsonLinesFile file) : BaseExporter<Metric>
{
    public override ExportResult Export(in Batch<Metric> batch)
    {
        foreach (var metric in batch)
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
                file.Write(LineOf(metric, point));
        }

        return ExportResult.Success;
    }

    protected override bool OnShutdown(int timeoutMilliseconds)
    {
        file.Dispose();
        return true;
    }

    private static string LineOf(Metric metric, MetricPoint point)
    {
        return JsonLine.Of(json =>
        {
            json.WriteString("time", point.EndTime);
            json.WriteString("name", metric.Name);
            if (metric.Unit is { Length: > 0 } unit)
                json.WriteString("unit", unit);

            json.WriteStartObject("tags");
            foreach (var tag in point.Tags)
                JsonLine.WriteTag(json, tag.Key, tag.Value);
            json.WriteEndObject();

            switch (metric.MetricType)
            {
                case MetricType.LongSum or MetricType.LongSumNonMonotonic:
                    json.WriteNumber("sum", point.GetSumLong());
                    break;
                case MetricType.DoubleSum or MetricType.DoubleSumNonMonotonic:
                    json.WriteNumber("sum", point.GetSumDouble());
                    break;
                case MetricType.LongGauge:
                    json.WriteNumber("value", point.GetGaugeLastValueLong());
                    break;
                case MetricType.DoubleGauge:
                    json.WriteNumber("value", point.GetGaugeLastValueDouble());
                    break;
                case MetricType.Histogram:
                    WriteHistogram(json, point);
                    break;
                default:
                    json.WriteNumber("count", point.GetHistogramCount());
                    json.WriteNumber("sum", point.GetHistogramSum());
                    break;
            }
        });
    }

    private static void WriteHistogram(Utf8JsonWriter json, MetricPoint point)
    {
        json.WriteNumber("count", point.GetHistogramCount());
        json.WriteNumber("sum", point.GetHistogramSum());
        json.WriteStartObject("buckets");
        foreach (var bucket in point.GetHistogramBuckets())
        {
            if (bucket.BucketCount > 0)
                json.WriteNumber(double.IsPositiveInfinity(bucket.ExplicitBound) ? "+Inf" : bucket.ExplicitBound.ToString("R", CultureInfo.InvariantCulture), bucket.BucketCount);
        }

        json.WriteEndObject();
    }
}
