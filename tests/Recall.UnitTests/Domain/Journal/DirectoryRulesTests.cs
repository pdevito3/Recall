namespace Recall.UnitTests.Domain.Journal;

using Recall.Server.Domain.Corrections;
using Recall.Server.Domain.People;
using Recall.Server.Domain.UserSettings;
using Recall.Server.Exceptions;
using Shouldly;

public class DirectoryRulesTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Noon.AddDays(1);

    [Test]
    [Property("Rule", "SES-24")]
    public void the_label_of_a_person_is_trimmed()
    {
        // Act
        var person = Person.Create("ten_1", "usr_1", "  Sam ");

        // Assert
        person.Label.ShouldBe("Sam");
    }

    [Test]
    [Property("Rule", "SES-24")]
    public void a_blank_label_is_refused()
    {
        // Arrange
        var person = Person.Create("ten_1", "usr_1", "Sam");

        // Act
        var rename = () => person.Rename("   ");

        // Assert
        rename.ShouldThrow<ValidationException>().Property.ShouldBe("label");
        person.Label.ShouldBe("Sam");
    }

    [Test]
    [Property("Rule", "SES-25")]
    public void mishearings_are_trimmed_have_no_duplicate_and_do_not_hold_the_term()
    {
        // Act
        var correction = Correction.Create("ten_1", "usr_1", " Kubernetes ", [" kuber netties", "KUBER NETTIES", "kubernetes", "  "], true);

        // Assert
        correction.CanonicalTerm.ShouldBe("Kubernetes");
        correction.Mishearings.ShouldBe(["kuber netties"]);
        correction.HardReplace.ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "SES-25")]
    public void a_blank_term_is_refused()
    {
        // Act
        var create = () => Correction.Create("ten_1", "usr_1", " ", [], false);

        // Assert
        create.ShouldThrow<ValidationException>().Property.ShouldBe("canonicalTerm");
    }

    [Test]
    [Property("Rule", "SES-26")]
    public void location_capture_is_off_until_the_user_turns_it_on()
    {
        // Act
        var setting = UserSetting.DefaultsFor("ten_1", "usr_1");

        // Assert
        setting.LocationCaptureEnabled.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-3")]
    public void a_user_with_no_zone_uses_utc()
    {
        // Act
        var zone = UserSetting.ZoneOf(null);

        // Assert
        zone.Value.ShouldBe("UTC");
    }

    [Test]
    public void an_unknown_zone_is_refused()
    {
        // Arrange
        var setting = UserSetting.DefaultsFor("ten_1", "usr_1");

        // Act
        Action change = () => setting.Change("Not/AZone", false, true, 0u);

        // Assert
        change.ShouldThrow<ValidationException>().Property.ShouldBe("timeZone");
    }
}
