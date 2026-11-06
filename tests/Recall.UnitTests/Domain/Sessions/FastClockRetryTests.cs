namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Metadata;
using Recall.Server.Domain.UserSettings;
using Shouldly;

public class FastClockRetryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Fast = Noon.AddHours(5);
    private static readonly DateTimeOffset FarFuture = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

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

    // The three times of a retry: the claim is still ahead of the server, the server sits between the claim and the
    // newest claim, and the server has passed each claim.
    [Test]
    [Property("Rule", "SES-26")]
    [Arguments(17, 1)]
    [Arguments(100, 310)]
    [Arguments(100, 600)]
    public void a_retry_of_a_setting_does_nothing_after_any_count_of_newer_fast_changes(int newerChanges, int minutesLater)
    {
        // Arrange
        var setting = NewSetting();
        setting.Change(null, true, false, Fast, Noon);
        foreach (var change in Enumerable.Range(1, newerChanges))
            setting.Change(null, false, false, Fast.AddMinutes(change), Noon.AddSeconds(change));
        var savedAt = setting.SavedAt;

        // Act
        setting.Change(null, true, false, Fast, Noon.AddMinutes(minutesLater));

        // Assert
        setting.LocationCaptureEnabled.ShouldBeFalse();
        setting.SavedAt.ShouldBe(savedAt);
    }

    [Test]
    [Property("Rule", "SES-21")]
    [Arguments(17, 1)]
    [Arguments(100, 310)]
    [Arguments(100, 600)]
    public void a_retry_of_metadata_does_nothing_after_any_count_of_newer_fast_changes(int newerChanges, int minutesLater)
    {
        // Arrange
        var session = NewSession();
        session.SetMetadata(Activity("Walking"), Fast, Noon);
        foreach (var change in Enumerable.Range(1, newerChanges))
            session.SetMetadata(Activity("Resting"), Fast.AddMinutes(change), Noon.AddSeconds(change));
        var savedAt = session.MetadataSavedAt;

        // Act
        session.SetMetadata(Activity("Walking"), Fast, Noon.AddMinutes(minutesLater));

        // Assert
        session.Activity.ShouldBe("Resting");
        session.MetadataSavedAt.ShouldBe(savedAt);
    }

    [Test]
    [Property("Rule", "SES-26")]
    public void a_setting_with_the_time_of_the_stored_change_does_nothing_so_its_retry_undoes_nothing()
    {
        // Arrange
        var setting = NewSetting();
        var time = Noon.AddHours(-1);
        setting.Change(null, false, false, time, Noon);

        // Act
        setting.Change(null, true, false, time, Noon.AddSeconds(1));

        // Assert
        setting.LocationCaptureEnabled.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-21")]
    public void metadata_with_the_time_of_the_stored_change_does_nothing_so_its_retry_undoes_nothing()
    {
        // Arrange
        var session = NewSession();
        var time = Noon.AddHours(-1);
        session.SetMetadata(Activity("Resting"), time, Noon);

        // Act
        session.SetMetadata(Activity("Walking"), time, Noon.AddSeconds(1));

        // Assert
        session.Activity.ShouldBe("Resting");
    }

    [Test]
    [Property("Rule", "SES-26")]
    public void a_time_that_the_database_cuts_compares_as_it_is_stored()
    {
        // Arrange
        var setting = NewSetting();
        var time = Noon.AddHours(-1).AddTicks(1234567);
        setting.Change(null, false, false, time, Noon);

        // Act
        setting.Change(null, true, false, time.AddTicks(1), Noon.AddSeconds(1));

        // Assert
        setting.LocationCaptureEnabled.ShouldBeFalse();
        setting.SavedAt.ShouldBe(Noon.AddHours(-1).AddTicks(1234560));
    }

    [Test]
    [Property("Rule", "SES-21")]
    public void a_distinct_older_change_from_a_fast_clock_applies_when_it_arrives_so_it_replaces_the_newer_change()
    {
        // Arrange
        var session = NewSession();
        session.SetMetadata(Activity("Resting"), Fast.AddMinutes(5), Noon);

        // Act
        session.SetMetadata(Activity("Walking"), Fast.AddMinutes(1), Noon.AddMinutes(1));

        // Assert
        session.Activity.ShouldBe("Walking");
    }

    [Test]
    [Property("Rule", "SES-26")]
    public void a_different_change_with_the_time_of_a_kept_claim_looks_like_its_retry_and_does_nothing()
    {
        // Arrange
        var setting = NewSetting();
        setting.Change(null, true, false, FarFuture, Noon);

        // Act
        setting.Change(null, false, false, FarFuture, Noon.AddMinutes(1));

        // Assert
        setting.LocationCaptureEnabled.ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "SES-26")]
    [Arguments(6)]
    [Arguments(60)]
    [Arguments(3600)]
    public void location_capture_off_from_a_fast_clock_takes_effect_behind_a_far_future_claim(int secondsFast)
    {
        // Arrange
        var setting = NewSetting();
        setting.Change(null, true, false, FarFuture, Noon);
        var later = Noon.AddMinutes(1);

        // Act
        setting.Change(null, false, false, later.AddSeconds(secondsFast), later);

        // Assert
        setting.LocationCaptureEnabled.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-21")]
    [Arguments(6)]
    [Arguments(60)]
    [Arguments(3600)]
    public void metadata_from_a_fast_clock_takes_effect_behind_a_far_future_claim(int secondsFast)
    {
        // Arrange
        var session = NewSession();
        session.SetMetadata(Activity("Resting"), FarFuture, Noon);
        var later = Noon.AddMinutes(1);

        // Act
        session.SetMetadata(Activity("Walking"), later.AddSeconds(secondsFast), later);

        // Assert
        session.Activity.ShouldBe("Walking");
    }

    [Test]
    [Property("Rule", "SES-26")]
    public void a_setting_that_arrives_behind_a_far_future_claim_and_its_retry_do_not_undo_a_newer_change()
    {
        // Arrange
        var setting = NewSetting();
        setting.Change(null, false, false, FarFuture, Noon);
        var sixSecondsFast = Noon.AddSeconds(66);
        setting.Change(null, true, false, sixSecondsFast, Noon.AddSeconds(60));
        setting.Change(null, false, false, Noon.AddSeconds(61), Noon.AddSeconds(61));

        // Act
        setting.Change(null, true, false, sixSecondsFast, Noon.AddMilliseconds(62_500));

        // Assert
        setting.LocationCaptureEnabled.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-21")]
    public void metadata_that_arrives_behind_a_far_future_claim_and_its_retry_do_not_undo_a_newer_change()
    {
        // Arrange
        var session = NewSession();
        session.SetMetadata(Activity("Resting"), FarFuture, Noon);
        var sixSecondsFast = Noon.AddSeconds(66);
        session.SetMetadata(Activity("Walking"), sixSecondsFast, Noon.AddSeconds(60));
        session.SetMetadata(Activity("Resting"), Noon.AddSeconds(61), Noon.AddSeconds(61));

        // Act
        session.SetMetadata(Activity("Walking"), sixSecondsFast, Noon.AddMilliseconds(62_500));

        // Assert
        session.Activity.ShouldBe("Resting");
    }

    [Test]
    [Property("Rule", "SES-26")]
    public void a_retry_of_a_far_future_claim_does_not_undo_a_newer_change()
    {
        // Arrange
        var setting = NewSetting();
        setting.Change(null, true, false, FarFuture, Noon);
        setting.Change(null, false, false, Noon.AddMinutes(1), Noon.AddMinutes(1));

        // Act
        setting.Change(null, true, false, FarFuture, Noon.AddYears(1));

        // Assert
        setting.LocationCaptureEnabled.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-21")]
    public void a_distinct_older_change_from_a_fast_clock_compares_as_itself_after_the_server_has_passed_it()
    {
        // Arrange
        var session = NewSession();
        session.SetMetadata(Activity("Resting"), Fast.AddMinutes(5), Noon);

        // Act
        session.SetMetadata(Activity("Walking"), Fast.AddMinutes(1), Fast.AddMinutes(10));

        // Assert
        session.Activity.ShouldBe("Walking");
    }

    [Test]
    [Property("Rule", "SES-26")]
    public void a_setting_from_a_slightly_fast_clock_wins_after_a_far_future_claim()
    {
        // Arrange
        var setting = NewSetting();
        setting.Change(null, true, false, FarFuture, Noon);

        // Act
        setting.Change(null, false, false, Noon.AddMinutes(1).AddMilliseconds(500), Noon.AddMinutes(1));

        // Assert
        setting.LocationCaptureEnabled.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-21")]
    public void metadata_from_a_slightly_fast_clock_wins_after_a_far_future_claim()
    {
        // Arrange
        var session = NewSession();
        session.SetMetadata(Activity("Resting"), FarFuture, Noon);

        // Act
        session.SetMetadata(Activity("Walking"), Noon.AddMinutes(1).AddMilliseconds(500), Noon.AddMinutes(1));

        // Assert
        session.Activity.ShouldBe("Walking");
    }

    [Test]
    [Property("Rule", "SES-26")]
    public void a_retry_from_a_slightly_fast_clock_does_not_undo_a_newer_change_after_a_far_future_claim()
    {
        // Arrange
        var setting = NewSetting();
        setting.Change(null, true, false, FarFuture, Noon);
        var slightlyFast = Noon.AddMinutes(1).AddMilliseconds(500);
        setting.Change(null, false, false, slightlyFast, Noon.AddMinutes(1));
        setting.Change(null, true, false, slightlyFast.AddSeconds(1), Noon.AddMinutes(1).AddSeconds(1));

        // Act
        setting.Change(null, false, false, slightlyFast, Noon.AddMinutes(1).AddSeconds(2));

        // Assert
        setting.LocationCaptureEnabled.ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "SES-26")]
    public void a_retry_with_a_zone_that_is_not_valid_now_does_nothing()
    {
        // Arrange
        var setting = NewSetting();
        setting.Change("Europe/Riga", true, false, Noon, Noon);

        // Act
        setting.Change("Not/AZone", false, false, Noon, Noon.AddMinutes(1));

        // Assert
        setting.TimeZone.ShouldBe("Europe/Riga");
        setting.LocationCaptureEnabled.ShouldBeTrue();
    }

    [Test]
    public void a_row_keeps_a_fast_claim_only_until_its_save_time_passes_the_claim()
    {
        // Arrange
        var setting = NewSetting();
        setting.Change(null, true, false, Noon, Noon);
        var afterAGoodClock = setting.FastClaims.Count;
        setting.Change(null, true, false, Fast, Noon.AddMinutes(1));
        setting.Change(null, true, false, Fast.AddMinutes(1), Noon.AddMinutes(2));
        var afterTwoFastClaims = setting.FastClaims.Count;

        // Act
        setting.Change(null, false, false, Fast.AddMinutes(2), Fast.AddMinutes(2));

        // Assert
        afterAGoodClock.ShouldBe(0);
        afterTwoFastClaims.ShouldBe(2);
        setting.FastClaims.ShouldBeEmpty();
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
