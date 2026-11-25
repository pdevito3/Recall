namespace Recall.UnitTests.Domain.ImportBatches;

using Recall.Server.Domain.ImportBatches;
using Recall.Server.Domain.TimeZones;
using Shouldly;

/// <summary>Chicago moves to CDT (UTC-5) at 02:00 on 2026-03-08 and back to CST (UTC-6) at 02:00 on 2026-11-01.</summary>
public class ImportStartTimeTests
{
    private static readonly TimeZoneId Chicago = TimeZoneId.Of("America/Chicago");
    private static readonly DateOnly Summer = new(2026, 6, 10);

    [Test]
    [Property("Rule", "IMP-7")]
    public void positions_0_1_at_9pm_and_2_start_at_noon_9pm_and_two_minutes_past_noon()
    {
        // Act
        var starts = new[]
        {
            ImportStartTime.Of(Summer, 0, null, null, Chicago),
            ImportStartTime.Of(Summer, 1, new TimeOnly(21, 0), null, Chicago),
            ImportStartTime.Of(Summer, 2, null, null, Chicago),
        };

        // Assert
        starts.Select(start => Chicago.ToLocal(start)).ShouldBe(
        [
            (Summer, new TimeOnly(12, 0)),
            (Summer, new TimeOnly(21, 0)),
            (Summer, new TimeOnly(12, 2)),
        ]);
    }

    [Test]
    [Property("Rule", "IMP-7")]
    public void the_time_of_the_person_wins_over_the_time_of_the_agent()
    {
        // Act
        var start = ImportStartTime.Of(Summer, 5, new TimeOnly(21, 0), new TimeOnly(7, 15), Chicago);

        // Assert
        Chicago.ToLocal(start).ShouldBe((Summer, new TimeOnly(7, 15)));
    }

    [Test]
    [Property("Rule", "IMP-7")]
    public void a_time_in_the_spring_gap_moves_to_the_first_minute_after_it()
    {
        // Act
        var start = ImportStartTime.Of(new DateOnly(2026, 3, 8), 0, new TimeOnly(2, 30), null, Chicago);

        // Assert
        start.ShouldBe(new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero));
    }

    [Test]
    [Property("Rule", "IMP-7")]
    public void a_time_that_occurs_twice_at_the_fall_back_takes_the_first()
    {
        // Act
        var start = ImportStartTime.Of(new DateOnly(2026, 11, 1), 0, new TimeOnly(1, 30), null, Chicago);

        // Assert
        start.ShouldBe(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero));
    }

    [Test]
    [Property("Rule", "IMP-7")]
    public void the_last_position_starts_on_the_date_of_the_entry()
    {
        // Act
        var start = ImportStartTime.Of(Summer, ImportAgentLayer.PositionMax, null, null, Chicago);

        // Assert
        Chicago.ToLocal(start).ShouldBe((Summer, new TimeOnly(23, 59)));
    }
}
