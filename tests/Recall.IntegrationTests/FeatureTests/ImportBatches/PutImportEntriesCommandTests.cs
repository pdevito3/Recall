namespace Recall.IntegrationTests.FeatureTests.ImportBatches;

using Recall.Server.Domain.ImportBatches;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Features;
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
        var scope = ScopeOf(owner);
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
}
