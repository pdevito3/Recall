namespace Recall.UnitTests.Domain.UserSettings;

using Recall.Server.Domain.UserSettings;
using Recall.Server.Exceptions;
using Shouldly;

public class TimeZoneSettingTests
{
    private static readonly TimeZoneSetting Riga = new TimeZoneSetting.FollowDevice("Europe/Riga");

    [Test]
    [Property("Rule", "SES-46")]
    public void a_report_moves_a_setting_that_follows_the_device()
    {
        // Act
        var next = Riga.WithReportedZone("Asia/Tokyo");

        // Assert
        next.ShouldBe(new TimeZoneSetting.FollowDevice("Asia/Tokyo"));
    }

    [Test]
    [Property("Rule", "SES-46")]
    public void a_report_does_not_move_a_fixed_setting()
    {
        // Arrange
        var paris = new TimeZoneSetting.Fixed("Europe/Paris");

        // Act
        var next = paris.WithReportedZone("Asia/Tokyo");

        // Assert
        next.ShouldBe(paris);
    }

    [Test]
    [Property("Rule", "SES-46")]
    public void a_report_of_a_zone_that_is_not_iana_is_refused()
    {
        // Act
        var report = () => Riga.WithReportedZone("Mars/Olympus_Mons");

        // Assert
        report.ShouldThrow<ValidationException>();
    }

    [Test]
    [Property("Rule", "SES-46")]
    [Arguments("Fixed", "Europe/Paris")]
    [Arguments("FollowDevice", "Asia/Tokyo")]
    public void a_request_with_a_zone_takes_its_mode_and_zone(string mode, string zone)
    {
        // Act
        var parsed = TimeZoneSetting.Parse(mode, zone, Riga);

        // Assert
        (parsed.Mode.ToString(), parsed.Zone).ShouldBe((mode, zone));
    }

    [Test]
    [Property("Rule", "SES-46")]
    [Arguments(null)]
    [Arguments("  ")]
    public void a_request_to_follow_the_device_with_no_zone_keeps_the_zone_that_holds(string? zone)
    {
        // Act
        var parsed = TimeZoneSetting.Parse("FollowDevice", zone, new TimeZoneSetting.Fixed("Europe/Paris"));

        // Assert
        parsed.ShouldBe(new TimeZoneSetting.FollowDevice("Europe/Paris"));
    }

    [Test]
    [Property("Rule", "SES-46")]
    [Arguments("Fixed", null)]
    [Arguments("fixed", "Europe/Paris")]
    [Arguments("Automatic", "Europe/Paris")]
    [Arguments(null, "Europe/Paris")]
    [Arguments("1", "Europe/Paris")]
    public void a_request_with_no_mode_or_a_fixed_mode_with_no_zone_is_refused(string? mode, string? zone)
    {
        // Act
        var parse = () => TimeZoneSetting.Parse(mode, zone, Riga);

        // Assert
        parse.ShouldThrow<ValidationException>();
    }
}
