namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.Sessions;
using Shouldly;

public class DraftSlotTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static DraftSave Save(string text, int baseRevision, DateTimeOffset savedAt) => DraftSave.Of(text, baseRevision, savedAt);

    private static DraftSlot At(int revision, DateTimeOffset savedAt) => new("current words", revision, revision, savedAt);

    [Test]
    [Property("Rule", "SES-5")]
    [Arguments(-3)]
    [Arguments(0)]
    [Arguments(5)]
    public void a_save_from_the_current_draft_wins_at_any_clock(int hours)
    {
        // Arrange
        var slot = At(2, Noon);

        // Act
        var decision = slot.Decide(Save("new words", 2, Noon.AddHours(hours)));

        // Assert
        decision.Outcome.ShouldBe(DraftSaveOutcome.Won);
        decision.Next.Text.ShouldBe("new words");
        decision.Next.DraftRevision.ShouldBe(3);
        decision.Next.SavedAt.ShouldBe(Noon.AddHours(hours));
    }

    [Test]
    [Property("Rule", "SES-5")]
    [Property("Rule", "SES-6")]
    [Arguments(0, -3)]
    [Arguments(0, 5)]
    [Arguments(3, -3)]
    [Arguments(3, 5)]
    [Arguments(5, 5)]
    [Arguments(999_999, 5)]
    public void a_save_from_any_other_base_loses_at_any_clock_and_takes_the_next_number(int baseRevision, int hours)
    {
        // Arrange
        var slot = new DraftSlot("current words", 4, 6, Noon);

        // Act
        var decision = slot.Decide(Save("other words", baseRevision, Noon.AddHours(hours)));

        // Assert
        decision.Outcome.ShouldBe(DraftSaveOutcome.Lost);
        decision.DraftRevision.ShouldBeNull();
        decision.Next.Text.ShouldBe("current words");
        decision.Next.DraftRevision.ShouldBe(4);
        decision.Next.SavedAt.ShouldBe(Noon);
        decision.Next.LatestRevision.ShouldBe(7);
        decision.MintedRevision.ShouldBe(7);
    }

    [Test]
    [Property("Rule", "SES-5")]
    public void of_two_saves_from_one_base_the_first_to_arrive_wins_in_either_order()
    {
        // Arrange
        var start = At(1, Noon);
        var earlier = Save("earlier", 1, Noon.AddMinutes(1));
        var later = Save("later", 1, Noon.AddMinutes(2));

        // Act
        var earlierFirst = start.Decide(earlier).Next.Decide(later);
        var laterFirst = start.Decide(later).Next.Decide(earlier);

        // Assert
        earlierFirst.Outcome.ShouldBe(DraftSaveOutcome.Lost);
        earlierFirst.Next.Text.ShouldBe("earlier");
        laterFirst.Outcome.ShouldBe(DraftSaveOutcome.Lost);
        laterFirst.Next.Text.ShouldBe("later");
    }

    [Test]
    [Property("Rule", "SES-8")]
    public void a_replay_of_a_save_that_lost_makes_no_revision_and_says_that_it_lost()
    {
        // Arrange
        var slot = At(4, Noon);

        // Act
        var decision = slot.Decide(Save("older words", 1, Noon.AddHours(-1)), new DraftAnswer(DraftTookAt: null));

        // Assert
        decision.Outcome.ShouldBe(DraftSaveOutcome.Lost);
        decision.DraftRevision.ShouldBeNull();
        decision.MintedRevision.ShouldBeNull();
        decision.Next.ShouldBe(slot);
    }

    [Test]
    [Property("Rule", "SES-8")]
    public void a_replay_of_a_save_that_won_says_kept_and_gives_the_number_at_which_it_won()
    {
        // Arrange
        var slot = At(4, Noon);

        // Act
        var decision = slot.Decide(Save("older words", 1, Noon.AddHours(-1)), new DraftAnswer(DraftTookAt: 2));

        // Assert
        decision.Outcome.ShouldBe(DraftSaveOutcome.Kept);
        decision.DraftRevision.ShouldBe(2);
        decision.MintedRevision.ShouldBeNull();
        decision.Next.ShouldBe(slot);
    }

    [Test]
    [Property("Rule", "SES-8")]
    public void a_replay_answers_before_the_words_of_the_draft_do()
    {
        // Arrange
        var slot = At(4, Noon);

        // Act
        var replayOfALoss = slot.Decide(Save("current words", 1, Noon), new DraftAnswer(DraftTookAt: null));
        var replayOfAWin = slot.Decide(Save("current words", 3, Noon), new DraftAnswer(DraftTookAt: 4));

        // Assert
        replayOfALoss.Outcome.ShouldBe(DraftSaveOutcome.Lost);
        replayOfAWin.Outcome.ShouldBe(DraftSaveOutcome.Kept);
    }

    [Test]
    [Property("Rule", "SES-8")]
    [Arguments(4)]
    [Arguments(1)]
    public void a_save_of_the_words_of_the_draft_makes_no_revision_from_any_base(int baseRevision)
    {
        // Arrange
        var slot = At(4, Noon);

        // Act
        var decision = slot.Decide(Save("current words", baseRevision, Noon.AddMinutes(5)));

        // Assert
        decision.Outcome.ShouldBe(DraftSaveOutcome.Unchanged);
        decision.DraftRevision.ShouldBe(4);
        decision.MintedRevision.ShouldBeNull();
        decision.Next.ShouldBe(slot);
    }

    [Test]
    [Property("Rule", "SES-9")]
    public void a_rewrite_moves_the_number_and_the_save_time_so_a_save_from_before_it_loses()
    {
        // Arrange
        var slot = new DraftSlot("current words", 4, 6, Noon);

        // Act
        var rewritten = slot.Rewritten("new label", Noon.AddHours(1));
        var fromBefore = rewritten.Decide(Save("old label", 4, Noon.AddHours(2)));

        // Assert
        rewritten.ShouldBe(new DraftSlot("new label", 7, 7, Noon.AddHours(1)));
        fromBefore.Outcome.ShouldBe(DraftSaveOutcome.Lost);
    }
}
