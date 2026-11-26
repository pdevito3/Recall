namespace Recall.IntegrationTests.FeatureTests.ImportBatches;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Recall.Server.Domain.ImportBatches;
using Recall.Server.Domain.ImportBatches.Dtos;
using Recall.Server.Domain.ImportBatches.Features;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Features;
using Recall.Server.Domain.Sync.Features;
using Recall.Server.Exceptions;
using Shouldly;
using static ImportScenario;

public class PutImportEntriesCommandTests : TestBase
{
    [Test]
    [Property("Rule", "IMP-3")]
    [Property("Rule", "IMP-17")]
    public async Task the_same_put_again_gives_unchanged_and_does_not_move_the_version()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        var first = await PutAsync(owner, batch.Id, Entry("a", 0), Entry("b", 1, time: "25:00"));

        // Act
        var again = await PutAsync(owner, batch.Id, Entry("a", 0), Entry("b", 1, time: "25:00"));

        // Assert
        OutcomesOf(first).ShouldBe([("a", "Added"), ("b", "Invalid")]);
        OutcomesOf(again).ShouldBe([("a", "Unchanged"), ("b", "Invalid")]);
        again.Outcomes[1].Field.ShouldBe("time");
        again.Version.ShouldBe(first.Version);
        (await EntriesAsync(owner, batch.Id)).Version.ShouldBe(first.Version);
    }

    [Test]
    [Property("Rule", "IMP-3")]
    [Property("Rule", "IMP-17")]
    public async Task a_put_that_changes_an_entry_moves_the_version_and_removes_no_other_entry()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        var first = await PutAsync(owner, batch.Id, Entry("a", 0), Entry("b", 1));

        // Act
        var changed = await PutAsync(owner, batch.Id, Entry("a", 0, raw: "other words"));

        // Assert
        OutcomesOf(changed).ShouldBe([("a", "Changed")]);
        changed.Version.ShouldNotBe(first.Version);
        var stored = await EntriesAsync(owner, batch.Id);
        stored.Entries.Select(e => (e.ClientSessionId, e.Raw)).ShouldBe([("a", "other words"), ("b", "words")]);
        stored.Version.ShouldBe(changed.Version);
    }

    [Test]
    [Property("Rule", "IMP-6")]
    [Property("Rule", "IMP-13")]
    public async Task a_rejected_entry_survives_a_put_a_remove_and_a_later_put()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0));
        await RejectAsync(owner, batch.Id, "a");

        // Act
        var put = await PutAsync(owner, batch.Id, Entry("a", 0, raw: "agent words"));
        var remove = await RemoveAsync(owner, batch.Id, "a");
        var later = await PutAsync(owner, batch.Id, Entry("a", 0, raw: "later words"));

        // Assert
        OutcomesOf(put).ShouldBe([("a", "Rejected")]);
        remove.Outcomes.Select(o => o.Outcome).ShouldBe(["Rejected"]);
        OutcomesOf(later).ShouldBe([("a", "Rejected")]);
        var entry = (await EntriesAsync(owner, batch.Id)).Entries.ShouldHaveSingleItem();
        entry.Raw.ShouldBe("later words");
        entry.Review.Rejected.ShouldBeTrue();
        entry.IsRejected.ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "IMP-6")]
    public async Task a_review_of_an_entry_that_the_agent_removed_gets_404_with_what_to_do_next()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0), Entry("b", 1));
        await RemoveAsync(owner, batch.Id, "a");

        // Act
        var review = () => RejectAsync(owner, batch.Id, "a");

        // Assert
        var refused = await review.ShouldThrowAsync<NotFoundException>();
        refused.Message.ShouldBe("This entry is no longer in the batch. Review the other entries of the batch.");
    }

    [Test]
    [Property("Rule", "IMP-6")]
    public async Task a_rejected_day_holds_an_entry_that_a_later_put_adds_and_an_entry_rejected_alone_stays_rejected()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0));
        await RejectAsync(owner, batch.Id, "a");
        await RejectDayAsync(owner, batch.Id, Day);

        // Act
        var added = await PutAsync(owner, batch.Id, Entry("b", 1));
        var held = await EntriesAsync(owner, batch.Id);
        await RejectDayAsync(owner, batch.Id, Day, rejected: false);
        var kept = await EntriesAsync(owner, batch.Id);

        // Assert
        OutcomesOf(added).ShouldBe([("b", "Rejected")]);
        held.RejectedDays.ShouldBe([Day]);
        held.Entries.Select(e => e.IsRejected).ShouldBe([true, true]);
        kept.RejectedDays.ShouldBeEmpty();
        kept.Entries.Select(e => (e.ClientSessionId, e.IsRejected)).ShouldBe([("a", true), ("b", false)]);
    }

    [Test]
    [Property("Rule", "IMP-6")]
    public async Task a_put_cannot_move_an_entry_out_of_a_rejected_day()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0));
        var rejected = await RejectDayAsync(owner, batch.Id, Day);

        // Act
        var put = await PutAsync(owner, batch.Id, Entry("a", 0, raw: "moved words", date: Day.AddDays(1)));

        // Assert
        OutcomesOf(put).ShouldBe([("a", "Rejected")]);
        put.Version.ShouldBe(rejected.Version);
        var entry = (await EntriesAsync(owner, batch.Id)).Entries.ShouldHaveSingleItem();
        (entry.Date, entry.Raw, entry.IsRejected).ShouldBe((Day, "words", true));
    }

    [Test]
    [Property("Rule", "IMP-13")]
    public async Task a_remove_deletes_the_named_entries_and_an_id_that_the_batch_does_not_hold_gets_not_found()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0), Entry("b", 1));

        // Act
        var removed = await RemoveAsync(owner, batch.Id, "a", "nothing");
        var again = await RemoveAsync(owner, batch.Id, "a");

        // Assert
        removed.Outcomes.Select(o => (o.ClientSessionId, o.Outcome)).ShouldBe([("a", "Removed"), ("nothing", "NotFound")]);
        again.Outcomes.Select(o => o.Outcome).ShouldBe(["NotFound"]);
        again.Version.ShouldBe(removed.Version);
        (await EntriesAsync(owner, batch.Id)).Entries.Select(e => e.ClientSessionId).ShouldBe(["b"]);
    }

    [Test]
    [Property("Rule", "IMP-28")]
    public async Task an_id_of_a_session_of_a_tombstone_or_of_another_batch_is_not_stored()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var other = await OpenAsync(owner);
        await PutAsync(owner, other.Id, Entry("elsewhere", 0));
        var scope = WebSessionOf(owner);
        await scope.SendAsync(new AddSession.Command(new SessionForCreationDto { ClientSessionId = "journal", StartedAt = DateTimeOffset.UtcNow.AddDays(-1) }));
        var deleted = await scope.SendAsync(new AddSession.Command(new SessionForCreationDto { ClientSessionId = "deleted", StartedAt = DateTimeOffset.UtcNow.AddDays(-1) }));
        await scope.SendAsync(new DeleteSession.Command(deleted.Session.Id));
        var batch = await OpenAsync(owner);

        // Act
        var put = await PutAsync(owner, batch.Id, Entry("journal", 0), Entry("deleted", 1), Entry("elsewhere", 2), Entry("new", 3));

        // Assert
        OutcomesOf(put).ShouldBe([("journal", "InJournal"), ("deleted", "Deleted"), ("elsewhere", "InOtherBatch"), ("new", "Added")]);
        (await EntriesAsync(owner, batch.Id)).Entries.Select(e => e.ClientSessionId).ShouldBe(["new"]);
    }

    [Test]
    [Property("Rule", "IMP-28")]
    public async Task a_call_that_names_one_id_twice_changes_nothing()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);

        // Act
        var put = () => PutAsync(owner, batch.Id, Entry("a", 0), Entry("a", 1));

        // Assert
        var refused = await put.ShouldThrowAsync<ValidationException>();
        refused.Property.ShouldBe("entries");
        refused.Message.ShouldBe(ImportBatch.PutCallMessage);
        (await EntriesAsync(owner, batch.Id)).Entries.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "IMP-28")]
    public async Task a_date_outside_the_range_gets_invalid_and_a_date_at_its_edge_is_judged()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);

        // Act
        var put = await PutAsync(owner, batch.Id,
            Entry("first-day", date: DateOnly.MinValue),
            Entry("before", date: new DateOnly(999, 12, 31)),
            Entry("first", date: new DateOnly(1000, 1, 1)),
            Entry("last", date: new DateOnly(9998, 12, 31)),
            Entry("after", date: new DateOnly(9999, 1, 1)),
            Entry("last-day", date: DateOnly.MaxValue));

        // Assert
        OutcomesOf(put).ShouldBe([
            ("first-day", "Invalid"), ("before", "Invalid"), ("first", "Added"), ("last", "InTheFuture"), ("after", "Invalid"), ("last-day", "Invalid"),
        ]);
        put.Outcomes.Where(o => o.Outcome == "Invalid").Select(o => o.Field).ShouldBe(["date", "date", "date", "date"]);
        (await EntriesAsync(owner, batch.Id)).Entries.Select(e => e.ClientSessionId).ShouldBe(["first"]);
    }

    [Test]
    [Property("Rule", "IMP-2")]
    public async Task an_entry_of_an_open_batch_is_in_no_reader_of_the_journal()
    {
        // Arrange
        var marker = $"PENDINGWORDS{Guid.NewGuid():N}";
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        var scope = WebSessionOf(owner);

        // Act
        await PutAsync(owner, batch.Id, Entry(marker, 0, raw: $"words {marker}"));
        var timeline = await scope.SendAsync(new GetSessionPage.Query(new CursorPageParametersDto()));
        var days = await scope.SendAsync(new GetJournalDays.Query(Day.AddDays(-1), Day.AddDays(1)));
        var feed = await scope.SendAsync(new GetSyncChangePage.Query(new CursorPageParametersDto()));

        // Assert
        (await EntriesAsync(owner, batch.Id)).Entries.ShouldHaveSingleItem().ClientSessionId.ShouldBe(marker);
        timeline.Items.ShouldBeEmpty();
        days.ShouldBeEmpty();
        feed.Sessions.ShouldBeEmpty();
        foreach (var read in new object[] { timeline, days, feed })
            JsonSerializer.Serialize(read).ShouldNotContain(marker);
    }

    [Test]
    [Property("Rule", "IMP-8")]
    public async Task a_put_can_swap_the_positions_of_two_stored_entries()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0), Entry("b", 1));

        // Act
        var swap = await PutAsync(owner, batch.Id, Entry("a", 1), Entry("b", 0));

        // Assert
        OutcomesOf(swap).ShouldBe([("a", "Changed"), ("b", "Changed")]);
        (await EntriesAsync(owner, batch.Id)).Entries.Select(e => (e.ClientSessionId, e.Position)).OrderBy(e => e.ClientSessionId)
            .ShouldBe([("a", 1), ("b", 0)]);
    }

    [Test]
    [Property("Rule", "IMP-8")]
    public async Task a_put_onto_a_taken_slot_gets_position_taken_with_the_other_id_and_leaves_the_stored_entry()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        var first = await PutAsync(owner, batch.Id, Entry("kept", 0), Entry("moving", 1));

        // Act
        var put = await PutAsync(owner, batch.Id, Entry("kept", 0), Entry("moving", 0, raw: "moved words"), Entry("new", 0));

        // Assert
        put.Outcomes.Select(o => (o.ClientSessionId, o.Outcome, o.PositionTakenBy)).ShouldBe([
            ("kept", "Unchanged", null), ("moving", "PositionTaken", "kept"), ("new", "PositionTaken", "kept"),
        ]);
        put.Version.ShouldBe(first.Version);
        (await EntriesAsync(owner, batch.Id)).Entries.Select(e => (e.ClientSessionId, e.Position, e.Raw)).OrderBy(e => e.ClientSessionId)
            .ShouldBe([("kept", 0, "words"), ("moving", 1, "words")]);
    }

    [Test]
    [Property("Rule", "IMP-9")]
    public async Task an_entry_that_starts_after_now_gets_in_the_future_and_is_not_stored()
    {
        // Arrange
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var today = new DateOnly(2026, 11, 25);
        await using var services = TestFixture.BuildServiceProvider(s => s.AddSingleton<TimeProvider>(new FakeTimeProvider(now)));
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);

        // Act
        var put = await PutAsync(WebSessionOf(owner, services), batch.Id,
            Entry("late", 0, date: today, time: "23:59"), Entry("noon", 1, date: today, time: "12:00"), Entry("tomorrow", 2, date: today.AddDays(1)));

        // Assert
        OutcomesOf(put).ShouldBe([("late", "InTheFuture"), ("noon", "Added"), ("tomorrow", "InTheFuture")]);
        (await EntriesAsync(owner, batch.Id)).Entries.Select(e => e.ClientSessionId).ShouldBe(["noon"]);
    }

    [Test]
    [Property("Rule", "IMP-17")]
    public async Task a_remove_an_entry_review_and_a_day_review_that_change_a_row_each_move_the_version_and_return_it()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        var put = await PutAsync(owner, batch.Id, Entry("a", 0), Entry("b", 1));

        // Act
        var removed = await RemoveAsync(owner, batch.Id, "a");
        var afterRemove = (await EntriesAsync(owner, batch.Id)).Version;
        var reviewed = await RejectAsync(owner, batch.Id, "b");
        var afterReview = (await EntriesAsync(owner, batch.Id)).Version;
        var day = await RejectDayAsync(owner, batch.Id, Day);
        var afterDay = (await EntriesAsync(owner, batch.Id)).Version;

        // Assert
        new[] { put.Version, afterRemove, afterReview, afterDay }.Distinct().Count().ShouldBe(4);
        (removed.Version, reviewed.Version, day.Version).ShouldBe((afterRemove, afterReview, afterDay));
    }

    [Test]
    [Property("Rule", "IMP-28")]
    public async Task a_call_of_more_than_500_entries_gets_422_and_stores_nothing_and_500_entries_pass()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        ImportEntryPutDto[] Entries(int count) => Enumerable.Range(0, count).Select(i => Entry($"entry-{i}", i % 720, date: Day.AddDays(i / 720))).ToArray();

        // Act
        var tooMany = () => PutAsync(owner, batch.Id, Entries(ImportBatch.PutMaxEntries + 1));
        var refused = await tooMany.ShouldThrowAsync<ValidationException>();
        var afterRefusal = await EntriesAsync(owner, batch.Id);
        var put = await PutAsync(owner, batch.Id, Entries(ImportBatch.PutMaxEntries));

        // Assert
        refused.Message.ShouldBe(ImportBatch.PutCallMessage);
        afterRefusal.Entries.ShouldBeEmpty();
        put.Outcomes.Select(o => o.Outcome).Distinct().ShouldBe(["Added"]);
        (await EntriesAsync(owner, batch.Id)).Entries.Count.ShouldBe(ImportBatch.PutMaxEntries);
    }

    [Test]
    [Property("Rule", "IMP-7")]
    public async Task a_reject_an_edit_and_a_put_of_one_entry_leave_the_start_of_another()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0), Entry("b", 1));
        DateTimeOffset StartOfB(ImportEntryPageDto page) => page.Entries.Single(e => e.ClientSessionId == "b").StartsAt;
        var before = StartOfB(await EntriesAsync(owner, batch.Id));

        // Act
        await RejectAsync(owner, batch.Id, "a");
        var afterReject = StartOfB(await EntriesAsync(owner, batch.Id));
        await WebSessionOf(owner).SendAsync(new ReviewImportEntry.Command(batch.Id,
            new ImportEntryReviewDto { ClientSessionId = "a", Rejected = false, Time = "08:00" }));
        var afterEdit = StartOfB(await EntriesAsync(owner, batch.Id));
        await PutAsync(owner, batch.Id, Entry("a", 5, time: "21:00"));
        var afterPut = StartOfB(await EntriesAsync(owner, batch.Id));

        // Assert
        before.ShouldBe(new DateTimeOffset(Day.ToDateTime(new TimeOnly(12, 1)), TimeSpan.Zero));
        new[] { afterReject, afterEdit, afterPut }.ShouldAllBe(start => start == before);
    }
}
