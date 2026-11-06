namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Sessions;
using Shouldly;

public class CapturedLocationTests
{
    [Test]
    [Property("Rule", "SES-18")]
    public void capture_off_stores_no_location()
    {
        // Act
        var location = CapturedLocation.Admit(false, 59.4, 24.7);

        // Assert
        location.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "SES-18")]
    [Arguments(59.4, null)]
    [Arguments(null, 24.7)]
    [Arguments(91.0, 24.7)]
    [Arguments(59.4, 181.0)]
    [Arguments(double.NaN, 24.7)]
    public void a_partial_point_or_a_point_not_on_the_globe_is_not_stored(double? latitude, double? longitude)
    {
        // Act
        var location = CapturedLocation.Admit(true, latitude, longitude);

        // Assert
        location.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "SES-18")]
    public void capture_on_stores_the_point()
    {
        // Act
        var location = CapturedLocation.Admit(true, 59.4, 24.7);

        // Assert
        location.ShouldNotBeNull();
        location.Latitude.ShouldBe(59.4);
        location.Longitude.ShouldBe(24.7);
    }
}
