namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Sessions;
using Shouldly;

public class DraftSlotTests
{
    [Test]
    public void a_save_that_misses_a_transcript_loses_at_any_time_and_stays_as_a_revision()
    {
        // Arrange
        var slot = new DraftSlot("heard words", 6, 6, new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var save = DraftSave.Of("old copy", 5, new DateTimeOffset(2026, 6, 1, 13, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero));

        // Act
        var decision = slot.Decide(save, isReplay: false, missesTranscript: true);

        // Assert
        decision.Outcome.ShouldBe(DraftSaveOutcome.Lost);
        decision.Next.Text.ShouldBe("heard words");
        decision.MintedRevision.ShouldBe(7);
    }

    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Noon.AddDays(1);

    private static DraftSave Save(string text, int baseRevision, DateTimeOffset savedAt) => DraftSave.Of(text, baseRevision, savedAt, Now);

    private static DraftSlot At(int revision, DateTimeOffset savedAt) => new("current words", revision, revision, savedAt);

    [Test]
    [Property("Rule", "SES-5")]
    public void a_save_based_on_the_current_draft_wins_with_an_older_clock()
    {
        // Arrange
        var slot = At(2, Noon);

        // Act
        var decision = slot.Decide(Save("new words", 2, Noon.AddHours(-3)), isReplay: false);

        // Assert
        decision.Outcome.ShouldBe(DraftSaveOutcome.Won);
        decision.Next.Text.ShouldBe("new words");
        decision.Next.DraftRevision.ShouldBe(3);
        decision.Next.SavedAt.ShouldBe(Noon, "A slow clock cannot move the save time of the Draft back.");
    }

    [Test]
    [Property("Rule", "SES-5")]
    public void of_two_stale_saves_the_later_save_time_wins_in_either_order_of_arrival()
    {
        // Arrange
        var start = At(1, Noon);
        var earlier = Save("earlier", 0, Noon.AddMinutes(1));
        var later = Save("later", 0, Noon.AddMinutes(2));

        // Act
        var earlierFirst = start.Decide(earlier, false).Next.Decide(later, false).Next;
        var laterFirst = start.Decide(later, false).Next.Decide(earlier, false).Next;

        // Assert
        earlierFirst.Text.ShouldBe("later");
        laterFirst.Text.ShouldBe("later");
    }

    [Test]
    [Property("Rule", "SES-5")]
    public void of_two_equal_save_times_the_save_that_arrives_last_wins()
    {
        // Arrange
        var slot = At(1, Noon);

        // Act
        var decision = slot.Decide(Save("same time", 0, Noon), false);

        // Assert
        decision.Outcome.ShouldBe(DraftSaveOutcome.Won);
        decision.Next.Text.ShouldBe("same time");
    }

    [Test]
    [Property("Rule", "SES-6")]
    public void a_save_that_loses_keeps_the_draft_and_takes_the_next_revision_number()
    {
        // Arrange
        var slot = At(4, Noon);

        // Act
        var decision = slot.Decide(Save("older words", 1, Noon.AddHours(-1)), false);

        // Assert
        decision.Outcome.ShouldBe(DraftSaveOutcome.Lost);
        decision.Next.Text.ShouldBe("current words");
        decision.Next.DraftRevision.ShouldBe(4);
        decision.Next.LatestRevision.ShouldBe(5);
        decision.MintedRevision.ShouldBe(5);
    }

    [Test]
    [Property("Rule", "SES-7")]
    public void a_save_time_in_the_future_becomes_the_time_of_the_server()
    {
        // Act
        var save = Save("words", 0, Now.AddYears(5));

        // Assert
        save.SavedAt.ShouldBe(Now);
    }

    [Test]
    [Property("Rule", "SES-8")]
    public void a_replay_of_a_kept_save_makes_no_revision()
    {
        // Arrange
        var slot = At(4, Noon);

        // Act
        var decision = slot.Decide(Save("older words", 1, Noon.AddHours(-1)), isReplay: true);

        // Assert
        decision.Outcome.ShouldBe(DraftSaveOutcome.Unchanged);
        decision.MintedRevision.ShouldBeNull();
        decision.Next.ShouldBe(slot);
    }

    [Test]
    [Property("Rule", "SES-8")]
    public void a_save_of_the_words_of_the_draft_makes_no_revision()
    {
        // Arrange
        var slot = At(4, Noon);

        // Act
        var decision = slot.Decide(Save("current words", 4, Noon.AddMinutes(5)), false);

        // Assert
        decision.Outcome.ShouldBe(DraftSaveOutcome.Unchanged);
        decision.MintedRevision.ShouldBeNull();
        decision.Next.ShouldBe(slot);
    }

    [Test]
    [Property("Rule", "SES-8")]
    public void a_later_offline_edit_keeps_the_slot_after_a_save_of_the_same_words()
    {
        // Arrange
        var slot = At(4, Noon);
        var resaved = slot.Decide(Save("current words", 4, Noon.AddMinutes(10)), false).Next;

        // Act
        var offline = resaved.Decide(Save("offline words", 3, Noon.AddMinutes(5)), false);

        // Assert
        offline.Outcome.ShouldBe(DraftSaveOutcome.Won);
        offline.Next.Text.ShouldBe("offline words");
    }

    [Test]
    public void only_a_stale_save_of_other_words_needs_a_read_of_the_revisions()
    {
        // Arrange
        var slot = At(4, Noon);

        // Act and Assert
        slot.NeedsReplayLookup(Save("other", 1, Noon)).ShouldBeTrue();
        slot.NeedsReplayLookup(Save("other", 4, Noon)).ShouldBeFalse();
        slot.NeedsReplayLookup(Save("current words", 1, Noon)).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-8")]
    public void a_replay_of_the_same_words_does_not_move_the_save_time()
    {
        // Arrange
        var slot = new DraftSlot("words", 1, 1, Noon);

        // Act
        var replay = slot.Decide(DraftSave.Of("words", 0, Noon.AddHours(5), Noon.AddMinutes(10)), false);

        // Assert
        replay.Outcome.ShouldBe(DraftSaveOutcome.Unchanged);
        replay.Next.ShouldBe(slot);
    }
}
