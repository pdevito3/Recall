namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Metadata;
using Recall.Server.Domain.UserSettings;
using Shouldly;

public class FastClockRetryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Fast = Noon.AddHours(5);

    private static Session NewSession() => Session.Create("ten_1", "usr_1", "device-1", Noon.AddDays(-1), null, Noon);

    private static SessionMetadata Activity(string activity) => SessionMetadata.Of(activity, null, null);

    private static UserSetting NewSetting() => UserSetting.DefaultsFor("ten_1", "usr_1");

    [Test]
    [Property("Rule", "SES-26")]
    [Arguments(1)]
    [Arguments(600)]
    public void a_retry_of_a_setting_from_a_fast_clock_does_not_turn_location_capture_on_again(int minutesLater)
    {
        // Arrange
        var setting = NewSetting();
        setting.Change(null, true, false, Fast, Noon);
        setting.Change(null, false, false, Fast.AddMinutes(1), Noon.AddSeconds(1));

        // Act
        setting.Change(null, true, false, Fast, Noon.AddMinutes(minutesLater));

        // Assert
        setting.LocationCaptureEnabled.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-26")]
    public void a_change_from_a_good_clock_beats_an_earlier_change_from_a_fast_clock_and_its_retry()
    {
        // Arrange
        var setting = NewSetting();
        setting.Change(null, true, false, Fast, Noon);

        // Act
        setting.Change(null, false, false, Noon.AddMinutes(1), Noon.AddMinutes(1));
        setting.Change(null, true, false, Fast, Noon.AddMinutes(2));

        // Assert
        setting.LocationCaptureEnabled.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-21")]
    [Arguments(1)]
    [Arguments(600)]
    public void a_retry_of_metadata_from_a_fast_clock_does_not_undo_a_newer_change(int minutesLater)
    {
        // Arrange
        var session = NewSession();
        session.SetMetadata(Activity("Walking"), Fast, Noon);
        session.SetMetadata(Activity("Resting"), Noon.AddSeconds(1), Noon.AddSeconds(1));

        // Act
        session.SetMetadata(Activity("Walking"), Fast, Noon.AddMinutes(minutesLater));

        // Assert
        session.Activity.ShouldBe("Resting");
    }

    [Test]
    [Property("Rule", "SES-21")]
    public void a_new_metadata_change_from_the_same_fast_clock_still_wins()
    {
        // Arrange
        var session = NewSession();
        session.SetMetadata(Activity("Walking"), Fast, Noon);

        // Act
        session.SetMetadata(Activity("Resting"), Fast.AddMinutes(1), Noon.AddMinutes(1));

        // Assert
        session.Activity.ShouldBe("Resting");
    }

    [Test]
    public void a_row_remembers_only_fast_claims_and_only_the_newest_ones()
    {
        // Arrange
        IReadOnlyList<long> claims = [];

        // Act
        var honest = claims.Remembering(Noon, Noon);
        foreach (var minute in Enumerable.Range(0, FastClockClaims.MaxCount + 1))
            claims = claims.Remembering(Fast.AddMinutes(minute), Noon);

        // Assert
        honest.ShouldBeEmpty();
        claims.Count.ShouldBe(FastClockClaims.MaxCount);
        claims.HoldsRetryOf(Fast).ShouldBeFalse();
        claims.HoldsRetryOf(Fast.AddMinutes(FastClockClaims.MaxCount)).ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "SES-3")]
    public void a_session_stores_its_start_as_utc_at_the_precision_of_the_database()
    {
        // Arrange
        var claimed = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.FromHours(5)).AddTicks(1234567);

        // Act
        var session = Session.Create("ten_1", "usr_1", "device-1", claimed, null, Noon);

        // Assert
        session.StartedAt.Offset.ShouldBe(TimeSpan.Zero);
        session.StartedAt.ShouldBe(new DateTimeOffset(2026, 5, 1, 5, 0, 0, TimeSpan.Zero).AddTicks(1234560));
    }
}
