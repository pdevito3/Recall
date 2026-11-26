namespace Recall.IntegrationTests.Databases;

using Microsoft.EntityFrameworkCore;
using Recall.Server.Databases;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.People.Features;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Features;
using Recall.Server.Domain.TimeZones;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;
using Recall.Server.Domain.UserSettings;
using Recall.Server.Domain.UserSettings.Dtos;
using Recall.Server.Domain.UserSettings.Features;
using Recall.Server.Exceptions;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public class JournalRaceTests : TestBase
{
    private static readonly DateTimeOffset Morning = DateTimeOffset.UtcNow.AddHours(-3);
    private static readonly TimeSpan WaitStep = TimeSpan.FromMilliseconds(20);

    [Test]
    [Property("Rule", "SES-1")]
    public async Task two_creates_with_one_client_session_id_make_one_session()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var pause = PauseAtQuery.After("sessions.add.tombstone");
        await using var services = pause.BuildServices();
        var dto = new SessionForCreationDto { ClientSessionId = Guid.NewGuid().ToString(), StartedAt = Morning };

        // Act
        var add = ScopeOf(owner, services).SendAsync(new AddSession.Command(dto));
        await pause.ReachedAsync();
        var rival = await ScopeOf(owner).SendAsync(new AddSession.Command(dto));
        pause.Release();

        // Assert
        var replay = await add;
        rival.IsNew.ShouldBeTrue();
        replay.IsNew.ShouldBeFalse();
        replay.Session.Id.ShouldBe(rival.Session.Id);
    }

    [Test]
    [Property("Rule", "SES-5")]
    public async Task a_raw_save_that_a_rival_beat_after_the_load_runs_again_and_loses_with_its_words_in_a_revision()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var session = await AddSessionAsync(owner);
        var pause = PauseAtQuery.After("sessions.save_raw.load");
        await using var services = pause.BuildServices();

        // Act
        var save = ScopeOf(owner, services).SendAsync(new SaveRawDraft.Command(session.Id, Save("newer", Morning.AddMinutes(2))));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new SaveRawDraft.Command(session.Id, Save("older", Morning.AddMinutes(1))));
        pause.Release();

        // Assert
        (await save).Outcome.ShouldBe("Lost");
        var stored = await ScopeOf(owner).SendAsync(new GetSession.Query(session.Id));
        stored.RawText.ShouldBe("older");
        (await RawTextsAsync(owner, session.Id)).ShouldBe(["older", "newer"]);
    }

    [Test]
    [Property("Rule", "SES-5")]
    public async Task a_cleaned_save_that_a_rival_beat_after_the_load_runs_again_and_loses_with_its_words_in_a_revision()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var session = await AddSessionAsync(owner);
        var pause = PauseAtQuery.After("sessions.save_cleaned.load");
        await using var services = pause.BuildServices();

        // Act
        var save = ScopeOf(owner, services).SendAsync(new SaveCleanedDraft.Command(session.Id, Save("newer", Morning.AddMinutes(2))));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new SaveCleanedDraft.Command(session.Id, Save("older", Morning.AddMinutes(1))));
        pause.Release();

        // Assert
        (await save).Outcome.ShouldBe("Lost");
        var stored = await ScopeOf(owner).SendAsync(new GetSession.Query(session.Id));
        stored.CleanedText.ShouldBe("older");
        stored.CleanedLatestRevision.ShouldBe(2);
    }

    [Test]
    [Property("Rule", "SES-8")]
    public async Task a_raw_replay_whose_first_try_saved_after_the_load_runs_again_and_names_a_latest_number_no_older_than_its_draft()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var session = await AddSessionAsync(owner);
        await ScopeOf(owner).SendAsync(new SaveRawDraft.Command(session.Id, Save("base", Morning)));
        var words = new DraftSaveDto { Text = "words", BaseRevision = 1, SavedAt = Morning.AddMinutes(1) };
        var pause = PauseAtQuery.After("sessions.save_raw.load");
        await using var services = pause.BuildServices();

        // Act
        var replay = ScopeOf(owner, services).SendAsync(new SaveRawDraft.Command(session.Id, words));
        await pause.ReachedAsync();
        var first = await ScopeOf(owner).SendAsync(new SaveRawDraft.Command(session.Id, words));
        pause.Release();
        var reply = await replay;

        // Assert
        first.Outcome.ShouldBe("Won");
        reply.Outcome.ShouldBe("Kept");
        reply.DraftRevision.ShouldBe(2);
        reply.LatestRevision.ShouldBe(2);
    }

    [Test]
    [Property("Rule", "SES-8")]
    public async Task a_cleaned_replay_whose_first_try_saved_after_the_load_runs_again_and_names_a_latest_number_no_older_than_its_draft()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var session = await AddSessionAsync(owner);
        await ScopeOf(owner).SendAsync(new SaveCleanedDraft.Command(session.Id, Save("base", Morning)));
        var words = new DraftSaveDto { Text = "words", BaseRevision = 1, SavedAt = Morning.AddMinutes(1) };
        var pause = PauseAtQuery.After("sessions.save_cleaned.load");
        await using var services = pause.BuildServices();

        // Act
        var replay = ScopeOf(owner, services).SendAsync(new SaveCleanedDraft.Command(session.Id, words));
        await pause.ReachedAsync();
        var first = await ScopeOf(owner).SendAsync(new SaveCleanedDraft.Command(session.Id, words));
        pause.Release();
        var reply = await replay;

        // Assert
        first.Outcome.ShouldBe("Won");
        reply.Outcome.ShouldBe("Kept");
        reply.DraftRevision.ShouldBe(2);
        reply.LatestRevision.ShouldBe(2);
    }

    [Test]
    [Property("Rule", "SES-32")]
    public async Task a_metadata_change_that_a_draft_save_beat_after_the_load_runs_again_and_saves()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var session = await AddSessionAsync(owner);
        var pause = PauseAtQuery.After("sessions.set_metadata.load");
        await using var services = pause.BuildServices();

        // Act
        var change = ScopeOf(owner, services).SendAsync(new SetSessionMetadata.Command(session.Id, new SessionMetadataChangeDto { Moods = ["calm"], MetadataVersion = 0 }));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new SaveRawDraft.Command(session.Id, Save("words", Morning)));
        pause.Release();

        // Assert
        (await change).Moods.ShouldBe(["Calm"]);
        var stored = await ScopeOf(owner).SendAsync(new GetSession.Query(session.Id));
        stored.Moods.ShouldBe(["Calm"]);
        stored.RawText.ShouldBe("words");
    }

    [Test]
    [Property("Rule", "SES-32")]
    public async Task a_metadata_change_that_a_rival_metadata_change_beat_after_the_load_is_refused()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var session = await AddSessionAsync(owner);
        var pause = PauseAtQuery.After("sessions.set_metadata.load");
        await using var services = pause.BuildServices();

        // Act
        var change = ScopeOf(owner, services).SendAsync(new SetSessionMetadata.Command(session.Id, new SessionMetadataChangeDto { UserTopics = ["Food"], MetadataVersion = 0 }));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new SetSessionMetadata.Command(session.Id, new SessionMetadataChangeDto { UserTopics = ["Work"], MetadataVersion = 0 }));
        pause.Release();

        // Assert
        (await Should.ThrowAsync<Exception>(change)).ShouldBeOfType<StaleMetadataVersionException>();
        var stored = await ScopeOf(owner).SendAsync(new GetSession.Query(session.Id));
        stored.Topics.ShouldHaveSingleItem().Name.ShouldBe("Work");
        stored.MetadataVersion.ShouldBe(1u);
    }

    [Test]
    [Property("Rule", "SES-34")]
    public async Task a_save_of_the_stored_metadata_that_a_rival_beat_after_the_load_is_refused()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var session = await AddSessionAsync(owner);
        await ScopeOf(owner).SendAsync(new SetSessionMetadata.Command(session.Id, new SessionMetadataChangeDto { UserTopics = ["Food"], MetadataVersion = 0 }));
        var pause = PauseAtQuery.After("sessions.set_metadata.load");
        await using var services = pause.BuildServices();

        // Act
        var change = ScopeOf(owner, services).SendAsync(new SetSessionMetadata.Command(session.Id, new SessionMetadataChangeDto { UserTopics = ["Food"], MetadataVersion = 1 }));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new SetSessionMetadata.Command(session.Id, new SessionMetadataChangeDto { UserTopics = ["Work"], MetadataVersion = 1 }));
        pause.Release();

        // Assert
        (await Should.ThrowAsync<Exception>(change)).ShouldBeOfType<StaleMetadataVersionException>();
        var stored = await ScopeOf(owner).SendAsync(new GetSession.Query(session.Id));
        stored.Topics.ShouldHaveSingleItem().Name.ShouldBe("Work");
        stored.MetadataVersion.ShouldBe(2u);
    }

    [Test]
    [Property("Rule", "SES-34")]
    public async Task a_metadata_retry_that_overlaps_the_first_try_runs_again_and_gets_the_stored_row()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var session = await AddSessionAsync(owner);
        var pause = PauseAtQuery.After("sessions.set_metadata.load");
        await using var services = pause.BuildServices();

        // Act
        var retry = ScopeOf(owner, services).SendAsync(new SetSessionMetadata.Command(session.Id, new SessionMetadataChangeDto { UserTopics = ["Food"], MetadataVersion = 0 }));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new SetSessionMetadata.Command(session.Id, new SessionMetadataChangeDto { UserTopics = ["Food"], MetadataVersion = 0 }));
        pause.Release();
        var reply = await retry;

        // Assert
        reply.Topics.ShouldHaveSingleItem().Name.ShouldBe("Food");
        reply.MetadataVersion.ShouldBe(1u);
    }

    [Test]
    [Property("Rule", "SES-34")]
    public async Task a_settings_retry_that_overlaps_the_first_try_is_refused_and_the_row_holds_its_values()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        await ScopeOf(owner).SendAsync(new ChangeUserSetting.Command(Setting("Europe/Riga", 0)));
        var pause = PauseAtQuery.After("user_settings.change.load");
        await using var services = pause.BuildServices();

        // Act
        var retry = ScopeOf(owner, services).SendAsync(new ChangeUserSetting.Command(Setting("Europe/Paris", 1)));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new ChangeUserSetting.Command(Setting("Europe/Paris", 1)));
        pause.Release();

        // Assert
        (await Should.ThrowAsync<Exception>(retry)).ShouldBeOfType<StaleVersionException>();
        var stored = await ScopeOf(owner).ExecuteDbContextAsync(db => db.UserSettings.SingleAsync());
        stored.TimeZone.ShouldBe(new TimeZoneSetting.Fixed("Europe/Paris"));
        stored.Version.ShouldBe(2u);
    }

    [Test]
    [Property("Rule", "SES-12")]
    public async Task a_delete_that_a_save_beat_after_the_load_runs_again_and_removes_the_session()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var session = await AddSessionAsync(owner);
        var pause = PauseAtQuery.After("sessions.delete.load");
        await using var services = pause.BuildServices();

        // Act
        var delete = ScopeOf(owner, services).SendAsync(new DeleteSession.Command(session.Id));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new SaveRawDraft.Command(session.Id, Save("words", Morning)));
        pause.Release();
        await delete;

        // Assert
        var read = () => ScopeOf(owner).SendAsync(new GetSession.Query(session.Id));
        await Should.ThrowAsync<NotFoundException>(read);
    }

    [Test]
    [Property("Rule", "SES-12")]
    public async Task a_save_that_a_delete_beat_after_the_load_gives_404_and_no_row_returns()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var session = await AddSessionAsync(owner);
        var pause = PauseAtQuery.After("sessions.save_raw.load");
        await using var services = pause.BuildServices();

        // Act
        var save = ScopeOf(owner, services).SendAsync(new SaveRawDraft.Command(session.Id, Save("words", Morning)));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new DeleteSession.Command(session.Id));
        pause.Release();

        // Assert
        await Should.ThrowAsync<NotFoundException>(save);
        (await RawTextsAsync(owner, session.Id)).ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-12")]
    public async Task a_session_with_an_owner_that_never_existed_is_a_server_fault_and_not_a_lost_race()
    {
        // Arrange
        var scope = new TestingServiceScope();
        var session = Session.Create(TestFixture.DefaultTenant.Id, "usr_00000000000000000000000000000000", Guid.NewGuid().ToString(), Morning, null, TimeZoneId.Utc, Morning);

        // Act
        var save = () => scope.AddAsync(session);

        // Assert
        (await Should.ThrowAsync<Exception>(save)).ShouldBeOfType<DbUpdateException>();
    }

    [Test]
    [Property("Rule", "SES-13")]
    public async Task an_add_that_a_delete_of_its_owner_beat_saves_no_row()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var pause = PauseAtQuery.After("sessions.add.tombstone");
        await using var services = pause.BuildServices();
        var dto = new SessionForCreationDto { ClientSessionId = Guid.NewGuid().ToString(), StartedAt = Morning };

        // Act
        var add = ScopeOf(owner, services).SendAsync(new AddSession.Command(dto));
        await pause.ReachedAsync();

        // The delete does not go through the handler, which can wait for the lock that the add holds. So it always wins.
        await TestFixture.ExecuteOutOfBandAsync("UPDATE users SET is_deleted = 1 WHERE id = @id", ("@id", owner.Id));
        pause.Release();

        // Assert
        (await Should.ThrowAsync<Exception>(add)).ShouldBeOfType<ForbiddenAccessException>();
        var rows = await new TestingServiceScope().ExecuteDbContextAsync(db => db.Sessions.IgnoreQueryFilters().CountAsync(s => s.UserId == owner.Id));
        rows.ShouldBe(0);
    }

    [Test]
    [Property("Rule", "SES-13")]
    public async Task an_add_and_a_delete_of_its_owner_at_one_time_leave_no_row()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var admin, u => u.Roles = [UserRole.Admin().Value])
            .Under(tenant)
            .AddUser(out var owner)
            .Under(tenant)
            .BuildAsync();
        scope.SetUserWithDefaults(identifier: admin.Identifier);
        var pause = PauseAtQuery.After("sessions.add.tombstone");
        await using var services = pause.BuildServices();
        var dto = new SessionForCreationDto { ClientSessionId = Guid.NewGuid().ToString(), StartedAt = Morning };

        // Act
        var add = ScopeOf(owner, services).SendAsync(new AddSession.Command(dto));
        await pause.ReachedAsync();
        var delete = scope.SendAsync(new DeleteUser.Command(owner.Id));
        while (!delete.IsCompleted && !RootLock.Tenant.HasWaiter(tenant.Id))
            await Task.Delay(WaitStep);
        pause.Release();
        await delete;
        var addFailure = await add.ContinueWith(t => t.Exception?.InnerException, TaskScheduler.Default);

        // Assert
        // Either order obeys the rule. The add can save first, and then the purge removes the row. Or the delete can
        // save first, and then the trigger refuses the add, which an_add_that_a_delete_of_its_owner_beat_saves_no_row proves.
        var rows = await new TestingServiceScope().ExecuteDbContextAsync(db => db.Sessions.IgnoreQueryFilters().CountAsync(s => s.UserId == owner.Id));
        rows.ShouldBe(0);
        (addFailure is null or ForbiddenAccessException).ShouldBeTrue(addFailure?.ToString());
    }

    [Test]
    [Property("Rule", "SES-9")]
    public async Task a_rename_that_a_save_beat_after_the_load_runs_again_and_relabels_the_new_words()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var person = await ScopeOf(owner).SendAsync(new AddPerson.Command(new PersonForCreationDto { Label = "Sam" }));
        var session = await AddSessionAsync(owner);
        await ScopeOf(owner).SendAsync(new SaveRawDraft.Command(session.Id, Save(Mention(person.Id, "Sam", "First"), Morning)));
        var pause = PauseAtQuery.After("people.rename.sessions");
        await using var services = pause.BuildServices();

        // Act
        var rename = ScopeOf(owner, services).SendAsync(new RenamePerson.Command(person.Id, new PersonLabelDto { Label = "Samuel" }));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new SaveRawDraft.Command(session.Id, new DraftSaveDto
        {
            Text = Mention(person.Id, "Sam", "Second"),
            BaseRevision = 1,
            SavedAt = Morning.AddMinutes(1),
        }));
        pause.Release();
        await rename;

        // Assert
        var stored = await ScopeOf(owner).SendAsync(new GetSession.Query(session.Id));
        stored.RawText.ShouldContain("Second");
        stored.RawText.ShouldContain("Samuel");
        stored.RawDraftRevision.ShouldBe(3);
    }

    [Test]
    [Property("Rule", "SES-47")]
    public async Task a_person_delete_that_a_save_beat_after_the_load_runs_again_and_unlinks_the_new_words()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var person = await ScopeOf(owner).SendAsync(new AddPerson.Command(new PersonForCreationDto { Label = "Sam" }));
        var session = await AddSessionAsync(owner);
        await ScopeOf(owner).SendAsync(new SaveRawDraft.Command(session.Id, Save(Mention(person.Id, "Sam", "First"), Morning)));
        var pause = PauseAtQuery.After("people.delete.sessions");
        await using var services = pause.BuildServices();

        // Act
        var delete = ScopeOf(owner, services).SendAsync(new DeletePerson.Command(person.Id, person.Version));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new SaveRawDraft.Command(session.Id, new DraftSaveDto
        {
            Text = Mention(person.Id, "Sam", "Second"),
            BaseRevision = 1,
            SavedAt = Morning.AddMinutes(1),
        }));
        pause.Release();
        await delete;

        // Assert
        var stored = await ScopeOf(owner).SendAsync(new GetSession.Query(session.Id));
        stored.RawText.ShouldContain("Second Sam");
        stored.PersonIds.ShouldBeEmpty();
        stored.RawDraftRevision.ShouldBe(3);
    }

    [Test]
    [Property("Rule", "SES-47")]
    public async Task a_person_delete_at_a_version_that_a_rename_moved_after_the_load_gives_a_stale_version_and_keeps_the_mention()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var person = await ScopeOf(owner).SendAsync(new AddPerson.Command(new PersonForCreationDto { Label = "Sam" }));
        var session = await AddSessionAsync(owner);
        await ScopeOf(owner).SendAsync(new SaveRawDraft.Command(session.Id, Save(Mention(person.Id, "Sam", "First"), Morning)));
        var pause = PauseAtQuery.After("people.delete.load");
        await using var services = pause.BuildServices();

        // Act
        var delete = ScopeOf(owner, services).SendAsync(new DeletePerson.Command(person.Id, person.Version));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new RenamePerson.Command(person.Id, new PersonLabelDto { Label = "Samuel" }));
        pause.Release();

        // Assert
        (await Should.ThrowAsync<Exception>(delete)).ShouldBeOfType<StaleSeenVersionException>();
        var stored = await ScopeOf(owner).SendAsync(new GetSession.Query(session.Id));
        stored.PersonIds.ShouldBe([person.Id]);
        stored.RawText.ShouldContain("Samuel");
    }

    [Test]
    [Property("Rule", "SES-31")]
    public async Task two_first_settings_changes_make_one_row_and_the_loser_is_refused()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var pause = PauseAtQuery.After("user_settings.change.load");
        await using var services = pause.BuildServices();

        // Act
        var change = ScopeOf(owner, services).SendAsync(new ChangeUserSetting.Command(Setting("Europe/Tallinn", 0)));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new ChangeUserSetting.Command(Setting("Europe/Paris", 0)));
        pause.Release();

        // Assert
        (await Should.ThrowAsync<Exception>(change)).ShouldBeOfType<StaleVersionException>();
        var stored = await ScopeOf(owner).ExecuteDbContextAsync(db => db.UserSettings.SingleAsync());
        stored.TimeZone.ShouldBe(new TimeZoneSetting.Fixed("Europe/Paris"));
    }

    [Test]
    [Property("Rule", "SES-34")]
    public async Task a_save_of_the_stored_settings_that_a_rival_beat_after_the_load_is_refused()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        await ScopeOf(owner).SendAsync(new ChangeUserSetting.Command(Setting("Europe/Riga", 0)));
        var pause = PauseAtQuery.After("user_settings.change.load");
        await using var services = pause.BuildServices();

        // Act
        var change = ScopeOf(owner, services).SendAsync(new ChangeUserSetting.Command(Setting("Europe/Riga", 1)));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new ChangeUserSetting.Command(Setting("Europe/Paris", 1)));
        pause.Release();

        // Assert
        (await Should.ThrowAsync<Exception>(change)).ShouldBeOfType<StaleVersionException>();
        var stored = await ScopeOf(owner).ExecuteDbContextAsync(db => db.UserSettings.SingleAsync());
        stored.TimeZone.ShouldBe(new TimeZoneSetting.Fixed("Europe/Paris"));
    }

    [Test]
    [Property("Rule", "SES-31")]
    public async Task a_settings_change_that_a_rival_beat_after_the_load_is_refused()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        await ScopeOf(owner).SendAsync(new ChangeUserSetting.Command(Setting("Europe/Riga", 0)));
        var pause = PauseAtQuery.After("user_settings.change.load");
        await using var services = pause.BuildServices();

        // Act
        var change = ScopeOf(owner, services).SendAsync(new ChangeUserSetting.Command(Setting("Europe/Tallinn", 1)));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new ChangeUserSetting.Command(Setting("Europe/Paris", 1)));
        pause.Release();

        // Assert
        (await Should.ThrowAsync<Exception>(change)).ShouldBeOfType<StaleVersionException>();
        var stored = await ScopeOf(owner).ExecuteDbContextAsync(db => db.UserSettings.SingleAsync());
        stored.TimeZone.ShouldBe(new TimeZoneSetting.Fixed("Europe/Paris"));
    }

    private static string Mention(string personId, string label, string words)
        => "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + words + " \"},"
            + "{\"type\":\"mention\",\"attrs\":{\"personId\":\"" + personId + "\",\"label\":\"" + label + "\"}}]}]}";

    private static DraftSaveDto Save(string text, DateTimeOffset savedAt)
        => new() { Text = text, BaseRevision = 0, SavedAt = savedAt };

    [Test]
    [Property("Rule", "SES-46")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_device_report_that_races_a_picked_zone_runs_again_and_the_picked_zone_holds(bool rowExists)
    {
        // Arrange
        var owner = await AddOwnerAsync();
        if (rowExists)
            await ScopeOf(owner).SendAsync(new ReportDeviceTimeZone.Command(new DeviceTimeZoneDto { TimeZone = "Europe/Riga" }));
        var version = rowExists ? 1u : 0u;
        var pause = PauseAtQuery.After("user_settings.report_zone.load");
        await using var services = pause.BuildServices();

        // Act
        var report = ScopeOf(owner, services).SendAsync(new ReportDeviceTimeZone.Command(new DeviceTimeZoneDto { TimeZone = "America/Chicago" }));
        await pause.ReachedAsync();
        await ScopeOf(owner).SendAsync(new ChangeUserSetting.Command(Setting("Europe/Paris", version)));
        pause.Release();

        // Assert
        (await report).TimeZone.ShouldBe("Europe/Paris");
        var stored = await ScopeOf(owner).ExecuteDbContextAsync(db => db.UserSettings.SingleAsync());
        stored.TimeZone.ShouldBe(new TimeZoneSetting.Fixed("Europe/Paris"));
        stored.Version.ShouldBe(version + 1);
    }

    private static UserSettingChangeDto Setting(string zone, uint version)
        => new() { TimeZoneMode = "Fixed", TimeZone = zone, LocationCaptureEnabled = false, RequirePeopleProposalApproval = true, Version = version };

    private static async Task<User> AddOwnerAsync()
    {
        await new TestingServiceScope().StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner)
            .Under(tenant)
            .BuildAsync();
        return owner;
    }

    private static TestingServiceScope ScopeOf(User owner, IServiceProvider? services = null)
    {
        var scope = services is null ? new TestingServiceScope() : new TestingServiceScope(services);
        scope.SetUserWithDefaults(identifier: owner.Identifier);
        return scope;
    }

    private static async Task<SessionDto> AddSessionAsync(User owner)
        => (await ScopeOf(owner).SendAsync(new AddSession.Command(new SessionForCreationDto
        {
            ClientSessionId = Guid.NewGuid().ToString(),
            StartedAt = Morning,
        }))).Session;

    private static Task<List<string>> RawTextsAsync(User owner, string sessionId)
        => ScopeOf(owner).ExecuteDbContextAsync(db => db.RawRevisions
            .Where(r => r.SessionId == sessionId)
            .OrderBy(r => r.Number)
            .Select(r => r.Text)
            .ToListAsync());
}
