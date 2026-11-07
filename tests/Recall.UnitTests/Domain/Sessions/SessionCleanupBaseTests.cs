namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Sessions;
using Recall.Server.Exceptions;
using Shouldly;

/// <summary>SES-5 for a Cleanup: a result replaces Cleaned only from the Cleaned number at the start of its run.</summary>
public class SessionCleanupBaseTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static CleanupOutcome Outcome(string cleaned) => new(cleaned, "A recap", [], [], []);

    private static Session CleanSession()
    {
        var session = Session.Create("ten_1", "usr_1", "device-1", Noon, null, Noon);
        session.SaveRaw(DraftSave.Of("raw words", 0, Noon));
        session.BeginCleanup();
        session.CompleteCleanup(Outcome("first clean words"), session.Raw.DraftRevision, Noon);
        return session;
    }

    [Test]
    public void a_hand_edit_during_a_run_stays_in_cleaned_and_the_result_waits_in_history()
    {
        // Arrange
        var session = CleanSession();
        session.BeginCleanup();
        session.SaveCleaned(DraftSave.Of("my own words", session.Cleaned.DraftRevision, Noon));
        var editNumber = session.Cleaned.DraftRevision;

        // Act
        session.CompleteCleanup(Outcome("second clean words"), session.Raw.DraftRevision, Noon);

        // Assert
        session.Cleaned.Text.ShouldBe("my own words");
        session.Cleaned.DraftRevision.ShouldBe(editNumber);
        session.CleanedHasHandEdits.ShouldBeTrue();
        session.WaitingCleanupRevision.ShouldBe(session.Cleaned.LatestRevision);
        session.WaitingCleanupRevision.ShouldNotBe(editNumber);
        session.CleanupStatus.ShouldBe(CleanupStatus.Clean);
    }

    [Test]
    public void a_hand_edit_before_a_run_is_replaced_and_nothing_waits()
    {
        // Arrange
        var session = CleanSession();
        session.SaveCleaned(DraftSave.Of("my own words", session.Cleaned.DraftRevision, Noon));
        session.BeginCleanup();

        // Act
        session.CompleteCleanup(Outcome("second clean words"), session.Raw.DraftRevision, Noon);

        // Assert
        session.Cleaned.Text.ShouldBe("second clean words");
        session.CleanedHasHandEdits.ShouldBeFalse();
        session.WaitingCleanupRevision.ShouldBeNull();
    }

    [Test]
    public void a_device_result_from_an_older_cleaned_base_keeps_cleaned_and_waits()
    {
        // Arrange
        var session = CleanSession();
        var deviceRead = session.Cleaned.DraftRevision;
        session.SaveCleaned(DraftSave.Of("my own words", deviceRead, Noon));

        // Act
        session.TakeDeviceCleanup(Outcome("device words"), session.Raw.DraftRevision, deviceRead, Noon, "hash");

        // Assert
        session.Cleaned.Text.ShouldBe("my own words");
        session.CleanedHasHandEdits.ShouldBeTrue();
        session.WaitingCleanupRevision.ShouldBe(session.Cleaned.LatestRevision);
    }

    [Test]
    public void a_device_result_from_the_current_cleaned_base_replaces_cleaned()
    {
        // Arrange
        var session = CleanSession();
        session.SaveCleaned(DraftSave.Of("my own words", session.Cleaned.DraftRevision, Noon));

        // Act
        session.TakeDeviceCleanup(Outcome("device words"), session.Raw.DraftRevision, session.Cleaned.DraftRevision, Noon, "hash");

        // Assert
        session.Cleaned.Text.ShouldBe("device words");
        session.CleanedHasHandEdits.ShouldBeFalse();
        session.WaitingCleanupRevision.ShouldBeNull();
    }

    [Test]
    public void using_the_waiting_result_puts_it_in_cleaned_only_from_the_current_draft()
    {
        // Arrange
        var session = CleanSession();
        session.BeginCleanup();
        session.SaveCleaned(DraftSave.Of("my own words", session.Cleaned.DraftRevision, Noon));
        session.CompleteCleanup(Outcome("second clean words"), session.Raw.DraftRevision, Noon);
        var waiting = session.WaitingCleanupRevision!.Value;
        var read = session.Cleaned.DraftRevision;

        // Act
        var fromOldBase = Should.Throw<ConflictException>(() => session.ApplyWaitingCleanup(waiting, "second clean words", read - 1, Noon));
        session.ApplyWaitingCleanup(waiting, "second clean words", read, Noon);
        var again = () => session.ApplyWaitingCleanup(waiting, "second clean words", session.Cleaned.DraftRevision, Noon);

        // Assert
        fromOldBase.Property.ShouldBe("cleanup");
        session.Cleaned.Text.ShouldBe("second clean words");
        session.Cleaned.DraftRevision.ShouldBeGreaterThan(waiting);
        session.CleanedHasHandEdits.ShouldBeFalse();
        session.WaitingCleanupRevision.ShouldBeNull();
        again.ShouldThrow<ConflictException>();
    }
}
