namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Mappings;
using Recall.Server.Domain.TimeZones;
using Shouldly;

public class JournalingDayTests
{
    private static readonly DateTimeOffset LateAtNight = new(2026, 6, 2, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 6, 3, 0, 0, 0, TimeSpan.Zero);

    [Test]
    [Property("Rule", "SES-3")]
    [Arguments("America/Chicago", 1)]
    [Arguments("UTC", 2)]
    [Arguments("Asia/Tokyo", 2)]
    public void the_journaling_day_comes_from_the_zone_of_the_session(string zone, int day)
    {
        // Arrange
        var session = Session.Create("ten_1", "usr_1", "device-1", LateAtNight, null, TimeZoneId.Of(zone), Later);

        // Act
        var journalingDay = session.ToSessionDto(ProposalPeople.Of([])).JournalingDay;

        // Assert
        journalingDay.ShouldBe(new DateOnly(2026, 6, day));
    }

    [Test]
    [Property("Rule", "SES-3")]
    public void a_zone_that_this_host_does_not_know_gives_the_utc_day()
    {
        // Act
        var journalingDay = Session.JournalingDayOf(LateAtNight, "Mars/Olympus_Mons");

        // Assert
        journalingDay.ShouldBe(new DateOnly(2026, 6, 2));
    }
}
