namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Mappings;
using Recall.Server.Domain.TimeZones;
using Shouldly;

public class JournalingDayTests
{
    [Test]
    [Property("Rule", "SES-3")]
    public void the_journaling_day_comes_from_the_time_zone_and_moves_with_it()
    {
        // Arrange
        var lateAtNight = new DateTimeOffset(2026, 6, 2, 2, 0, 0, TimeSpan.Zero);
        var session = Session.Create("ten_1", "usr_1", "device-1", lateAtNight, null, new DateTimeOffset(2026, 6, 3, 0, 0, 0, TimeSpan.Zero));

        // Act
        var chicago = session.ToSessionDto(TimeZoneId.Of("America/Chicago")).JournalingDay;
        var utc = session.ToSessionDto(TimeZoneId.Of("UTC")).JournalingDay;

        // Assert
        chicago.ShouldBe(new DateOnly(2026, 6, 1));
        utc.ShouldBe(new DateOnly(2026, 6, 2));
    }
}
