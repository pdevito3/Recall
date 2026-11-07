namespace Recall.UnitTests.Domain.Summaries;

using Recall.Server.Domain.Summaries;
using Shouldly;

public class SummarySourcesTests
{
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private static SessionStub Session(string id, DateOnly day, int raw = 1, int cleaned = 0) => new(id, day, raw, cleaned);

    private static PeriodKey Key(SummaryPeriod period, DateOnly day) => PeriodKey.Holding(period, day);

    [Test]
    public void a_day_and_a_week_count_their_sessions()
    {
        // Arrange
        var sources = new SummarySources([Session("ses_a", Monday), Session("ses_b", Monday), Session("ses_c", Monday), Session("ses_d", Monday.AddDays(3))]);

        // Act
        var day = sources.StateOf(Key(SummaryPeriod.Day, Monday));
        var week = sources.StateOf(Key(SummaryPeriod.Week, Monday));

        // Assert
        day.Count.ShouldBe(3);
        week.Count.ShouldBe(4);
        sources.SessionsIn(Key(SummaryPeriod.Day, Monday)).Select(s => s.Id).ShouldBe(["ses_a", "ses_b", "ses_c"]);
    }

    [Test]
    public void a_roll_up_counts_its_source_periods_with_sources()
    {
        // Arrange: three Days in November, one Day in December, and one Day in the next year
        var sources = new SummarySources([
            Session("ses_a", new DateOnly(2026, 11, 2)),
            Session("ses_b", new DateOnly(2026, 11, 2)),
            Session("ses_c", new DateOnly(2026, 11, 9)),
            Session("ses_d", new DateOnly(2026, 11, 30)),
            Session("ses_e", new DateOnly(2026, 12, 1)),
            Session("ses_f", new DateOnly(2027, 1, 1)),
        ]);

        // Act
        var month = sources.StateOf(Key(SummaryPeriod.Month, Monday));
        var quarter = sources.StateOf(Key(SummaryPeriod.Quarter, Monday));
        var year = sources.StateOf(Key(SummaryPeriod.Year, Monday));

        // Assert
        month.Count.ShouldBe(3);
        sources.SourcePeriodsWithSources(Key(SummaryPeriod.Month, Monday)).Select(k => k.Start.Day).ShouldBe([2, 9, 30]);
        quarter.Count.ShouldBe(2);
        year.Count.ShouldBe(1);
        sources.StateOf(Key(SummaryPeriod.Year, new DateOnly(2027, 1, 1))).Count.ShouldBe(1);
    }

    [Test]
    public void a_period_with_no_sessions_has_no_sources()
    {
        // Arrange
        var sources = new SummarySources([Session("ses_a", Monday)]);

        // Act
        var otherDay = sources.StateOf(Key(SummaryPeriod.Day, Monday.AddDays(1)));
        var otherYear = sources.StateOf(Key(SummaryPeriod.Year, new DateOnly(2025, 6, 1)));

        // Assert
        otherDay.ShouldBe(SourceState.None);
        otherDay.HasSources.ShouldBeFalse();
        otherYear.HasSources.ShouldBeFalse();
    }

    [Test]
    [Arguments(2, 0)]
    [Arguments(1, 1)]
    public void a_new_draft_of_a_source_changes_the_fingerprint_up_the_chain(int raw, int cleaned)
    {
        // Arrange: a Person rename and an edit each make a new Draft, so each bumps a number
        var before = new SummarySources([Session("ses_a", Monday), Session("ses_b", Monday.AddDays(1))]);
        var after = new SummarySources([Session("ses_a", Monday, raw, cleaned), Session("ses_b", Monday.AddDays(1))]);

        // Act and Assert
        foreach (var period in Enum.GetValues<SummaryPeriod>())
            after.StateOf(Key(period, Monday)).Fingerprint.ShouldNotBe(before.StateOf(Key(period, Monday)).Fingerprint, period.ToString());
        after.StateOf(Key(SummaryPeriod.Day, Monday.AddDays(1))).ShouldBe(before.StateOf(Key(SummaryPeriod.Day, Monday.AddDays(1))));
    }

    [Test]
    public void a_session_that_moves_to_another_day_or_goes_away_changes_the_fingerprint()
    {
        // Arrange
        var before = new SummarySources([Session("ses_a", Monday), Session("ses_b", Monday)]);
        var moved = new SummarySources([Session("ses_a", Monday), Session("ses_b", Monday.AddDays(1))]);
        var gone = new SummarySources([Session("ses_a", Monday)]);
        var month = Key(SummaryPeriod.Month, Monday);

        // Act and Assert
        moved.StateOf(month).Fingerprint.ShouldNotBe(before.StateOf(month).Fingerprint);
        gone.StateOf(month).Fingerprint.ShouldNotBe(before.StateOf(month).Fingerprint);
        moved.StateOf(Key(SummaryPeriod.Week, Monday)).Fingerprint.ShouldNotBe(before.StateOf(Key(SummaryPeriod.Week, Monday)).Fingerprint);
    }

    [Test]
    public void the_same_sources_in_any_order_have_the_same_fingerprint()
    {
        // Arrange
        var one = new SummarySources([Session("ses_a", Monday), Session("ses_b", Monday), Session("ses_c", Monday.AddDays(40))]);
        var other = new SummarySources([Session("ses_c", Monday.AddDays(40)), Session("ses_b", Monday), Session("ses_a", Monday)]);

        // Act and Assert
        foreach (var period in Enum.GetValues<SummaryPeriod>())
            other.StateOf(Key(period, Monday)).ShouldBe(one.StateOf(Key(period, Monday)));
    }
}
