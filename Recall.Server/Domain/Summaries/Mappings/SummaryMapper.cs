namespace Recall.Server.Domain.Summaries.Mappings;

using Dtos;

public static class SummaryMapper
{
    public static SummaryDto ToSummaryDto(this Summary? summary, PeriodKey key, SourceState sources)
    {
        var status = summary?.StatusFor(sources) ?? SummaryStatus.Missing;
        return new SummaryDto
        {
            Id = status == SummaryStatus.Missing ? null : summary!.Id,
            Period = key.Period.ToString(),
            PeriodDate = key.Start,
            PeriodLastDate = key.Last,
            Status = status.ToString(),
            Failure = (status == SummaryStatus.Failed ? summary!.Failure : SummaryFailure.None).ToString(),
            Content = status is SummaryStatus.Ready or SummaryStatus.Stale ? summary!.Content ?? string.Empty : string.Empty,
            SourceCount = sources.Count,
            GeneratedAt = status is SummaryStatus.Ready or SummaryStatus.Stale ? summary!.GeneratedAt : null,
        };
    }
}
