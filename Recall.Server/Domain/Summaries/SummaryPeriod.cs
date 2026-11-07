namespace Recall.Server.Domain.Summaries;

using Exceptions;

/// <summary>The length of time that one Summary covers. The order is the order of the roll-up, so a source has a lower value.</summary>
public enum SummaryPeriod
{
    Day = 0,
    Week = 1,
    Month = 2,
    Quarter = 3,
    Year = 4,
}

/// <summary>
/// One period of one User: its kind and its first Journaling day. A Week starts on a Monday. Month, Quarter, and Year start on
/// the first day of the calendar unit.
/// </summary>
public readonly record struct PeriodKey(SummaryPeriod Period, DateOnly Start)
{
    private const string PeriodField = "period";

    /// <summary>The period of <paramref name="period"/> that holds <paramref name="date"/>.</summary>
    public static PeriodKey Holding(SummaryPeriod period, DateOnly date) => new(period, period switch
    {
        SummaryPeriod.Day => date,
        SummaryPeriod.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        SummaryPeriod.Month => new DateOnly(date.Year, date.Month, 1),
        SummaryPeriod.Quarter => new DateOnly(date.Year, ((date.Month - 1) / 3 * 3) + 1, 1),
        SummaryPeriod.Year => new DateOnly(date.Year, 1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(period)),
    });

    /// <summary>Reads a period name from a route. A number is no name, so <c>0</c> is refused.</summary>
    public static SummaryPeriod ParsePeriod(string value)
    {
        var known = Enum.GetNames<SummaryPeriod>().FirstOrDefault(name => name.Equals(value, StringComparison.OrdinalIgnoreCase));
        ValidationException.Must(known is not null, PeriodField, "The period must be Day, Week, Month, Quarter, or Year.");
        return Enum.Parse<SummaryPeriod>(known!);
    }

    /// <summary>The first day after the period.</summary>
    public DateOnly End => Period switch
    {
        SummaryPeriod.Day => Start.AddDays(1),
        SummaryPeriod.Week => Start.AddDays(7),
        SummaryPeriod.Month => Start.AddMonths(1),
        SummaryPeriod.Quarter => Start.AddMonths(3),
        SummaryPeriod.Year => Start.AddYears(1),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>The last day of the period.</summary>
    public DateOnly Last => End.AddDays(-1);

    /// <summary>The kind of Summary that this period reads, or null when it reads Sessions.</summary>
    public SummaryPeriod? SourcePeriod => Period switch
    {
        SummaryPeriod.Month => SummaryPeriod.Day,
        SummaryPeriod.Quarter => SummaryPeriod.Month,
        SummaryPeriod.Year => SummaryPeriod.Quarter,
        _ => null,
    };

    public bool Contains(DateOnly day) => Start <= day && day < End;

    /// <summary>Each period of a roll-up source inside this one, in date order. Empty for a period that reads Sessions.</summary>
    public IEnumerable<PeriodKey> SourcePeriods()
    {
        if (SourcePeriod is not { } source)
            yield break;

        for (var start = Start; start < End; start = Holding(source, start).End)
            yield return Holding(source, start);
    }

    /// <summary>Each period that reads <paramref name="day"/>, directly or up the chain.</summary>
    public static IEnumerable<PeriodKey> AllHolding(DateOnly day) => Enum.GetValues<SummaryPeriod>().Select(period => Holding(period, day));

    public override string ToString() => $"{Period} {Start:yyyy-MM-dd}";
}
