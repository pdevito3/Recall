namespace Recall.IntegrationTests.FeatureTests.ImportBatches;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Domain.AuditEntries;
using Recall.Server.Domain.ImportBatches;
using Recall.Server.Domain.ImportBatches.Dtos;
using Recall.Server.Domain.ImportBatches.Features;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Features;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.UserSettings.Dtos;
using Recall.Server.Domain.UserSettings.Features;
using Recall.Server.Exceptions;
using Shouldly;
using static ImportScenario;

public class AcceptImportBatchCommandTests : TestBase
{
    private static readonly TokenGrant ReadWrite = new([], JournalAccess.ReadWrite);

    [Test]
    [Property("Rule", "IMP-14")]
    public async Task an_accept_makes_a_session_of_each_kept_entry_at_its_start_in_its_zone()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner, "America/Chicago");
        await PutAsync(owner, batch.Id, Entry("noon", 0), Entry("evening", 1, time: "21:00"),
            Entry("after", 2) with { Moods = ["calm"], Topics = ["work"] }, Entry("dropped", 3));
        await RejectAsync(owner, batch.Id, "dropped");

        // Act
        var accepted = await AcceptAsync(owner, batch.Id);

        // Assert
        accepted.State.ShouldBe("Accepted");
        accepted.Result.ShouldBe(new ImportAcceptResultDto { SessionsMade = 3, EntriesRejected = 1 });
        var sessions = await WebSessionOf(owner).ExecuteDbContextAsync(db => db.Sessions.OrderBy(s => s.StartedAt).ToListAsync());
        sessions.Select(s => (s.ClientSessionId, s.StartedAt.UtcDateTime, s.TimeZone, s.ImportBatchId)).ShouldBe([
            ("noon", new DateTime(2024, 3, 1, 18, 0, 0, DateTimeKind.Utc), "America/Chicago", batch.Id),
            ("after", new DateTime(2024, 3, 1, 18, 2, 0, DateTimeKind.Utc), "America/Chicago", batch.Id),
            ("evening", new DateTime(2024, 3, 2, 3, 0, 0, DateTimeKind.Utc), "America/Chicago", batch.Id),
        ]);
        var after = await WebSessionOf(owner).SendAsync(new GetSession.Query(sessions[1].Id));
        (after.RawDraftRevision, after.RawLatestRevision, after.CleanupStatus).ShouldBe((1, 1, "NotRun"));
        after.RawText.ShouldContain("words");
        after.Moods.ShouldBe(["Calm"]);
        after.Topics.ShouldBe([new SessionTopicDto { Name = "work", Provenance = "AiSuggested" }]);
        var left = await WebSessionOf(owner).ExecuteDbContextAsync(async db =>
            (await db.ImportEntries.CountAsync(), await db.ImportDays.CountAsync(), await db.People.CountAsync()));
        left.ShouldBe((0, 0, 0));
    }

    [Test]
    [Property("Rule", "IMP-10")]
    [Property("Rule", "SES-3")]
    public async Task an_accept_keeps_the_zone_of_the_import_when_the_settings_have_a_fixed_zone()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        await WebSessionOf(owner).SendAsync(new ChangeUserSetting.Command(new UserSettingChangeDto
        {
            TimeZoneMode = "Fixed", TimeZone = "Asia/Tokyo", LocationCaptureEnabled = false, RequirePeopleProposalApproval = true, Version = 0,
        }));
        var batch = await OpenAsync(owner, "America/Chicago");
        await PutAsync(owner, batch.Id, Entry("batch", 0, time: "23:00"), Entry("own", 1, time: "23:00") with { TimeZone = "Europe/Paris" });

        // Act
        await AcceptAsync(owner, batch.Id);

        // Assert
        var sessions = await WebSessionOf(owner).ExecuteDbContextAsync(db => db.Sessions.OrderBy(s => s.ClientSessionId).ToListAsync());
        sessions.Select(s => (s.ClientSessionId, s.TimeZone)).ShouldBe([("batch", "America/Chicago"), ("own", "Europe/Paris")]);
        foreach (var session in sessions)
            (await WebSessionOf(owner).SendAsync(new GetSession.Query(session.Id))).JournalingDay.ShouldBe(Day);
    }

    [Test]
    [Property("Rule", "IMP-16")]
    public async Task an_entry_whose_id_became_a_session_or_a_tombstone_after_its_put_makes_no_session()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("device", 0), Entry("gone", 1), Entry("kept", 2));
        var scope = WebSessionOf(owner);
        var device = await scope.SendAsync(new AddSession.Command(new SessionForCreationDto { ClientSessionId = "device", StartedAt = DateTimeOffset.UtcNow.AddDays(-1) }));
        var gone = await scope.SendAsync(new AddSession.Command(new SessionForCreationDto { ClientSessionId = "gone", StartedAt = DateTimeOffset.UtcNow.AddDays(-1) }));
        await scope.SendAsync(new DeleteSession.Command(gone.Session.Id));

        // Act
        var accepted = await AcceptAsync(owner, batch.Id);

        // Assert
        accepted.Result.ShouldBe(new ImportAcceptResultDto { SessionsMade = 1, SkippedInJournal = 1, SkippedDeleted = 1 });
        var sessions = await WebSessionOf(owner).ExecuteDbContextAsync(db => db.Sessions.OrderBy(s => s.ClientSessionId).ToListAsync());
        sessions.Select(s => (s.ClientSessionId, s.ImportBatchId)).ShouldBe([("device", null), ("kept", batch.Id)]);
        sessions[0].Id.ShouldBe(device.Session.Id);
    }

    [Test]
    [Property("Rule", "IMP-17")]
    public async Task an_accept_from_another_version_gets_409_and_changes_nothing()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        var put = await PutAsync(owner, batch.Id, Entry("a", 0));
        await PutAsync(owner, batch.Id, Entry("b", 1));

        // Act
        var accept = () => WebSessionOf(owner).SendAsync(new AcceptImportBatch.Command(batch.Id, new ImportBatchAcceptDto { Version = put.Version }));

        // Assert
        (await accept.ShouldThrowAsync<StaleVersionException>()).Message.ShouldBe(ImportBatch.ChangedMessage);
        (await EntriesAsync(owner, batch.Id)).Entries.Count.ShouldBe(2);
        (await WebSessionOf(owner).ExecuteDbContextAsync(db => db.Sessions.CountAsync())).ShouldBe(0);
    }

    [Test]
    [Property("Rule", "IMP-17")]
    public async Task an_accept_of_an_accepted_batch_gives_its_stored_result_and_writes_nothing()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0));
        var accepted = await AcceptAsync(owner, batch.Id);
        var native = new TestingServiceScope();
        native.SetAppSession(owner.Identifier, SessionKind.Native);

        // Act
        var again = await native.SendAsync(new AcceptImportBatch.Command(batch.Id, new ImportBatchAcceptDto { Version = 0 }));

        // Assert
        again.ShouldBe(accepted);
        (await ActionsOfAsync(owner, batch.Id)).Count.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IMP-18")]
    public async Task with_the_review_on_only_a_web_session_of_the_person_accepts()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0));
        var native = new TestingServiceScope();
        native.SetAppSession(owner.Identifier, SessionKind.Native);
        var token = new TestingServiceScope();
        token.SetPersonalAccessToken(owner.Identifier, ReadWrite);
        var provider = new TestingServiceScope();
        provider.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        var refusals = new List<ForbiddenAccessException>();
        foreach (var caller in new[] { native, token, provider })
            refusals.Add(await Should.ThrowAsync<ForbiddenAccessException>(() => AcceptAsync(caller, owner, batch.Id)));
        var accepted = await AcceptAsync(owner, batch.Id);

        // Assert
        refusals.Select(r => r.Message).ShouldBe(Enumerable.Repeat(AcceptImportBatch.ReviewMessage, 3));
        accepted.Result!.SessionsMade.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IMP-18")]
    [Property("Rule", "IMP-20")]
    public async Task with_the_review_off_a_token_with_journal_write_accepts_and_the_audit_log_records_it()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var settings = await WebSessionOf(owner).SendAsync(new ChangeImportReview.Command(new ImportReviewChangeDto { RequireImportReview = false, Version = 0 }));
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0), Entry("b", 1), Entry("c", 0, date: Day.AddDays(1)));
        await RejectAsync(owner, batch.Id, "b");
        await RejectDayAsync(owner, batch.Id, Day.AddDays(1));
        var token = new TestingServiceScope();
        token.SetPersonalAccessToken(owner.Identifier, ReadWrite);

        // Act
        var accepted = await AcceptAsync(token, owner, batch.Id);

        // Assert
        settings.RequireImportReview.ShouldBeFalse();
        accepted.Result.ShouldBe(new ImportAcceptResultDto { SessionsMade = 1, EntriesRejected = 2, DaysRejected = 1 });
        var action = (await ActionsOfAsync(owner, batch.Id)).ShouldHaveSingleItem();
        action.Action.ShouldBe("import_batch_accepted");
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(action.Data)!.Select(p => $"{p.Key}={p.Value}").Order().ShouldBe([
            "DaysRejected=1", "EntriesRejected=2", "SessionsMade=1", "SkippedDeleted=0", "SkippedInJournal=0",
        ]);
        var session = await WebSessionOf(owner).ExecuteDbContextAsync(db => db.Sessions.SingleAsync());
        var insert = await WebSessionOf(owner).ExecuteDbContextAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .SingleAsync(e => e.EntityId == session.Id && e.Kind == AuditEntryKind.Created));
        insert.RecordId.ShouldBe(action.RecordId);
        insert.Data.ShouldNotContain("words");
    }

    [Test]
    [Property("Rule", "IMP-21")]
    public async Task a_rejected_id_is_free_for_a_later_import_after_an_accept_and_after_a_discard()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var accepted = await OpenAsync(owner);
        await PutAsync(owner, accepted.Id, Entry("rejected", 0), Entry("kept", 1));
        await RejectAsync(owner, accepted.Id, "rejected");
        await AcceptAsync(owner, accepted.Id);
        var discarded = await OpenAsync(owner);
        await PutAsync(owner, discarded.Id, Entry("discarded", 0));
        await RejectAsync(owner, discarded.Id, "discarded");
        await WebSessionOf(owner).SendAsync(new DiscardImportBatch.Command(discarded.Id));
        var later = await OpenAsync(owner);

        // Act
        var put = await PutAsync(owner, later.Id, Entry("rejected", 0), Entry("discarded", 1), Entry("kept", 2));

        // Assert
        OutcomesOf(put).ShouldBe([("rejected", "Added"), ("discarded", "Added"), ("kept", "InJournal")]);
        var rows = await WebSessionOf(owner).ExecuteDbContextAsync(async db => (
            await db.SyncChanges.CountAsync(c => c.IsTombstone),
            await db.ImportBatches.Select(b => b.Id).ToListAsync()));
        rows.Item1.ShouldBe(0);
        rows.Item2.Order().ShouldBe(new[] { accepted.Id, later.Id }.Order());
    }

    [Test]
    [Property("Rule", "IMP-23")]
    public async Task a_put_a_remove_a_review_and_a_discard_of_an_accepted_batch_get_409_and_change_nothing()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0));
        var accepted = await AcceptAsync(owner, batch.Id);
        var scope = WebSessionOf(owner);

        // Act
        Func<Task>[] writes =
        [
            () => PutAsync(owner, batch.Id, Entry("b", 1)),
            () => RemoveAsync(owner, batch.Id, "a"),
            () => RejectAsync(owner, batch.Id, "a"),
            () => RejectDayAsync(owner, batch.Id, Day),
            () => scope.SendAsync(new DiscardImportBatch.Command(batch.Id)),
        ];
        var refusals = new List<ConflictException>();
        foreach (var write in writes)
            refusals.Add(await write.ShouldThrowAsync<ConflictException>());

        // Assert
        refusals.Select(r => r.Message).ShouldBe(Enumerable.Repeat(ImportBatch.AcceptedMessage, writes.Length));
        var stored = (await scope.SendAsync(new GetImportBatches.Query())).ShouldHaveSingleItem();
        stored.ShouldBe(accepted);
        (await scope.ExecuteDbContextAsync(db => db.Sessions.CountAsync())).ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IMP-23")]
    public async Task a_token_with_read_journal_access_gets_403_on_each_write_also_on_one_that_would_change_nothing()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a", 0));
        var version = (await EntriesAsync(owner, batch.Id)).Version;
        var reader = new TestingServiceScope();
        reader.SetPersonalAccessToken(owner.Identifier, new TokenGrant([], JournalAccess.Read));

        // Act
        Func<Task>[] writes =
        [
            () => reader.SendAsync(new OpenImportBatch.Command(new ImportBatchForCreationDto { Name = "Read", TimeZone = "UTC" })),
            () => PutAsync(reader, batch.Id, Entry("a", 0)),
            () => PutAsync(reader, batch.Id),
            () => reader.SendAsync(new RemoveImportEntries.Command(batch.Id, new ImportEntriesRemoveDto { ClientSessionIds = ["nothing"] })),
            () => reader.SendAsync(new ReviewImportEntry.Command(batch.Id, new ImportEntryReviewDto { ClientSessionId = "a", Rejected = false })),
            () => reader.SendAsync(new ReviewImportDay.Command(batch.Id, new ImportDayReviewDto { Date = Day, Rejected = false })),
            () => reader.SendAsync(new AcceptImportBatch.Command(batch.Id, new ImportBatchAcceptDto { Version = version })),
            () => reader.SendAsync(new DiscardImportBatch.Command(batch.Id)),
        ];
        var refusals = new List<ForbiddenAccessException>();
        foreach (var write in writes)
            refusals.Add(await write.ShouldThrowAsync<ForbiddenAccessException>());

        // Assert
        refusals.Select(r => r.Message).ShouldBe(Enumerable.Repeat(JournalWriteRule.Refusal, writes.Length));
        (await reader.SendAsync(new GetImportEntries.Query(batch.Id, null, null))).Version.ShouldBe(version);
        (await reader.SendAsync(new GetImportBatches.Query())).Select(b => b.Id).ShouldBe([batch.Id]);
    }

    [Test]
    [Property("Rule", "IMP-27")]
    [Property("Rule", "IMP-17")]
    public async Task an_accept_over_the_limit_is_refused_after_the_gate_of_the_review_and_changes_nothing()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        foreach (var call in Enumerable.Range(0, ImportBatch.AcceptMaxEntries + 1).Chunk(ImportBatch.PutMaxEntries))
            await PutAsync(owner, batch.Id, call.Select(i => Entry($"entry-{i}", i % 720, date: Day.AddDays(i / 720))).ToArray());
        var before = await EntriesAsync(owner, batch.Id);
        var token = new TestingServiceScope();
        token.SetPersonalAccessToken(owner.Identifier, ReadWrite);
        var command = new AcceptImportBatch.Command(batch.Id, new ImportBatchAcceptDto { Version = before.Version });

        // Act
        var byToken = () => token.SendAsync(command);
        var gated = await byToken.ShouldThrowAsync<ForbiddenAccessException>();
        var byPerson = () => WebSessionOf(owner).SendAsync(command);
        var limited = await byPerson.ShouldThrowAsync<ValidationException>();

        // Assert
        gated.Message.ShouldBe(AcceptImportBatch.ReviewMessage);
        limited.Message.ShouldBe(ImportBatch.TooManyToAcceptMessage);
        var after = await EntriesAsync(owner, batch.Id);
        (after.Version, after.Entries.Count).ShouldBe((before.Version, ImportBatch.AcceptMaxEntries + 1));
        (await WebSessionOf(owner).SendAsync(new GetImportBatches.Query())).ShouldHaveSingleItem().State.ShouldBe(nameof(ImportBatchState.Open));
        (await WebSessionOf(owner).ExecuteDbContextAsync(db => db.Sessions.CountAsync())).ShouldBe(0);
        (await ActionsOfAsync(owner, batch.Id)).ShouldBeEmpty();
    }

    private static Task<List<AuditEntry>> ActionsOfAsync(User owner, string batchId)
        => WebSessionOf(owner).ExecuteDbContextAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .Where(e => e.RootId == batchId && e.Kind == AuditEntryKind.Action)
            .ToListAsync());
}
