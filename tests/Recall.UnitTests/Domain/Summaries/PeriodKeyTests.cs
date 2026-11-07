namespace Recall.UnitTests.Domain.Summaries;

using Recall.Server.Domain.Summaries;
using Recall.Server.Exceptions;
using Shouldly;

public class PeriodKeyTests
{
    private static DateOnly Day(string value) => DateOnly.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    [Test]
    [Arguments(SummaryPeriod.Day, "2026-11-07", "2026-11-07", "2026-11-08")]
    [Arguments(SummaryPeriod.Week, "2026-11-07", "2026-11-02", "2026-11-09")]
    [Arguments(SummaryPeriod.Week, "2026-11-02", "2026-11-02", "2026-11-09")]
    [Arguments(SummaryPeriod.Week, "2026-11-08", "2026-11-02", "2026-11-09")]
    [Arguments(SummaryPeriod.Week, "2027-01-01", "2026-12-28", "2027-01-04")]
    [Arguments(SummaryPeriod.Month, "2026-02-14", "2026-02-01", "2026-03-01")]
    [Arguments(SummaryPeriod.Quarter, "2026-11-07", "2026-10-01", "2027-01-01")]
    [Arguments(SummaryPeriod.Quarter, "2026-03-31", "2026-01-01", "2026-04-01")]
    [Arguments(SummaryPeriod.Year, "2026-11-07", "2026-01-01", "2027-01-01")]
    public void a_period_holds_a_day_from_its_start_up_to_its_end(SummaryPeriod period, string day, string start, string end)
    {
        // Act
        var key = PeriodKey.Holding(period, Day(day));

        // Assert
        key.Start.ShouldBe(Day(start));
        key.End.ShouldBe(Day(end));
        key.Contains(Day(day)).ShouldBeTrue();
    }

    [Test]
    public void a_week_starts_on_a_monday()
    {
        // Act
        var starts = Enumerable.Range(0, 400).Select(offset => PeriodKey.Holding(SummaryPeriod.Week, Day("2026-01-01").AddDays(offset)).Start);

        // Assert
        starts.ShouldAllBe(start => start.DayOfWeek == DayOfWeek.Monday);
    }

    [Test]
    public void a_month_reads_its_days_a_quarter_its_months_and_a_year_its_quarters()
    {
        // Act
        var month = PeriodKey.Holding(SummaryPeriod.Month, Day("2026-02-10")).SourcePeriods().ToList();
        var quarter = PeriodKey.Holding(SummaryPeriod.Quarter, Day("2026-02-10")).SourcePeriods().ToList();
        var year = PeriodKey.Holding(SummaryPeriod.Year, Day("2026-02-10")).SourcePeriods().ToList();

        // Assert
        month.Count.ShouldBe(28);
        month.ShouldAllBe(key => key.Period == SummaryPeriod.Day);
        quarter.Select(key => key.Start).ShouldBe([Day("2026-01-01"), Day("2026-02-01"), Day("2026-03-01")]);
        year.Select(key => key.Start).ShouldBe([Day("2026-01-01"), Day("2026-04-01"), Day("2026-07-01"), Day("2026-10-01")]);
        PeriodKey.Holding(SummaryPeriod.Day, Day("2026-02-10")).SourcePeriods().ShouldBeEmpty();
        PeriodKey.Holding(SummaryPeriod.Week, Day("2026-02-10")).SourcePeriods().ShouldBeEmpty();
    }

    [Test]
    [Arguments("day", SummaryPeriod.Day)]
    [Arguments("Week", SummaryPeriod.Week)]
    [Arguments("QUARTER", SummaryPeriod.Quarter)]
    public void a_period_name_is_read_in_any_case(string value, SummaryPeriod expected)
    {
        // Act
        var parsed = PeriodKey.ParsePeriod(value);

        // Assert
        parsed.ShouldBe(expected);
    }

    [Test]
    [Arguments("0")]
    [Arguments("4")]
    [Arguments("fortnight")]
    [Arguments("")]
    public void a_number_or_an_unknown_name_is_no_period(string value)
    {
        // Act
        Action parse = () => PeriodKey.ParsePeriod(value);

        // Assert
        parse.ShouldThrow<ValidationException>();
    }
}
