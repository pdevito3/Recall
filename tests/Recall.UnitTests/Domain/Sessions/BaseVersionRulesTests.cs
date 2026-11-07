namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Metadata;
using Recall.Server.Domain.UserSettings;
using Recall.Server.Exceptions;
using Shouldly;

public class BaseVersionRulesTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static Session NewSession() => Session.Create("ten_1", "usr_1", "device-1", Noon.AddDays(-1), null, Noon);

    private static SessionMetadata Topics(params string[] topics) => SessionMetadata.Of(null, null, topics);

    private static UserSetting NewSetting() => UserSetting.DefaultsFor("ten_1", "usr_1");

    [Test]
    [Property("Rule", "SES-31")]
    public void a_settings_change_from_another_version_throws_and_changes_nothing()
    {
        // Arrange
        var setting = NewSetting();

        // Act
        Action change = () => setting.Change("Europe/Tallinn", true, false, 5u);

        // Assert
        change.ShouldThrow<StaleVersionException>();
        setting.LocationCaptureEnabled.ShouldBeFalse();
        setting.TimeZone.ShouldBeNull();
        setting.RequirePeopleProposalApproval.ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "SES-31")]
    public void a_stale_settings_change_with_a_zone_that_is_not_valid_gets_the_stale_refusal()
    {
        // Arrange
        var setting = NewSetting();

        // Act
        Action change = () => setting.Change("Not/AZone", true, false, 5u);

        // Assert
        change.ShouldThrow<StaleVersionException>();
    }

    [Test]
    [Property("Rule", "SES-34")]
    public void a_settings_change_with_the_stored_values_from_another_version_does_nothing()
    {
        // Arrange
        var setting = NewSetting();

        // Act
        var applied = setting.Change(null, false, true, 5u);

        // Assert
        applied.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-34")]
    public void a_settings_change_with_the_stored_values_from_the_current_version_takes_that_version()
    {
        // Arrange
        var setting = NewSetting();

        // Act
        var applied = setting.Change(null, false, true, 0u);

        // Assert
        applied.ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "SES-34")]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public void a_blank_zone_is_the_stored_none_so_it_does_not_look_like_a_change(string? zone)
    {
        // Arrange
        var setting = NewSetting();

        // Act
        var applied = setting.Change(zone, false, true, 5u);

        // Assert
        applied.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-32")]
    public void a_metadata_change_from_the_current_version_applies_and_moves_the_version()
    {
        // Arrange
        var session = NewSession();

        // Act
        var applied = session.SetMetadata(Topics("Food"), 0u);

        // Assert
        applied.ShouldBeTrue();
        session.UserTopics.ShouldBe(["Food"]);
        session.MetadataVersion.ShouldBe(1u);
    }

    [Test]
    [Property("Rule", "SES-32")]
    [Arguments(0u)]
    [Arguments(2u)]
    public void a_metadata_change_from_another_version_throws_and_changes_nothing(uint sent)
    {
        // Arrange
        var session = NewSession();
        session.SetMetadata(Topics("Food", "Work"), 0u);

        // Act
        Action change = () => session.SetMetadata(Topics("Food"), sent);

        // Assert
        change.ShouldThrow<StaleMetadataVersionException>();
        session.UserTopics.ShouldBe(["Food", "Work"]);
        session.MetadataVersion.ShouldBe(1u);
    }

    [Test]
    [Property("Rule", "SES-32")]
    public void a_draft_save_does_not_move_the_metadata_version()
    {
        // Arrange
        var session = NewSession();

        // Act
        session.SaveRaw(DraftSave.Of("words", 0, Noon));

        // Assert
        session.MetadataVersion.ShouldBe(0u);
    }

    [Test]
    [Property("Rule", "SES-34")]
    [Arguments(0u)]
    [Arguments(9u)]
    public void a_metadata_change_with_the_stored_values_from_another_version_does_nothing(uint sent)
    {
        // Arrange
        var session = NewSession();
        session.SetMetadata(Topics("Food"), 0u);

        // Act
        var applied = session.SetMetadata(Topics("Food"), sent);

        // Assert
        applied.ShouldBeFalse();
        session.MetadataVersion.ShouldBe(1u);
    }

    [Test]
    [Property("Rule", "SES-34")]
    public void a_metadata_change_with_the_stored_values_from_the_current_version_takes_that_version()
    {
        // Arrange
        var session = NewSession();
        session.SetMetadata(Topics("Food"), 0u);

        // Act
        var applied = session.SetMetadata(Topics("Food"), 1u);

        // Assert
        applied.ShouldBeTrue();
        session.MetadataVersion.ShouldBe(2u);
    }
}
