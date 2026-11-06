namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.UserSettings.Dtos;
using Shouldly;
using TestUtilities;

public class SessionApiTests : TestBase
{
    private static readonly string[] FirstTopic = ["first"];
    private static readonly string[] ShoutedTopic = [" WORK "];

    [Test]
    public async Task a_saved_text_comes_back_exactly()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        const string words = "  Söögiaeg \U0001F600 with \"quotes\"\nand a new line. <b>Not HTML</b> ";

        // Act
        var saved = await FactoryClient.SaveDraftAsync(session.Id, words, 0);
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        read!.RawText.ShouldBe(words);
        read.RawDraftRevision.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "SES-29")]
    [Arguments("""{"a":1,"a":2}""")]
    [Arguments("""{"type":"doc","type":"x"}""")]
    [Arguments("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"cut emoji \ud83d"}]}]}""")]
    [Arguments("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"mention","attrs":[]}]}]}""")]
    [Arguments("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"mention","attrs":1}]}]}""")]
    public async Task words_that_are_not_a_good_document_save_and_come_back_exactly(string words)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();

        // Act
        var raw = await FactoryClient.SaveDraftAsync(session.Id, words, 0);
        var cleaned = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Cleaned(session.Id), new { text = words, baseRevision = 0, savedAt = JournalScenario.Morning });
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        raw.StatusCode.ShouldBe(HttpStatusCode.OK);
        cleaned.StatusCode.ShouldBe(HttpStatusCode.OK);
        read!.RawText.ShouldBe(words);
        read.CleanedText.ShouldBe(words);
    }

    [Test]
    [Property("Rule", "SES-29")]
    public async Task a_raw_nul_gives_422_and_a_raw_lone_surrogate_gives_400()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        StringContent Body(string escape) => new($$"""{"text":"cut {{escape}} words","baseRevision":0,"savedAt":"2026-01-01T00:00:00Z"}""", Encoding.UTF8, "application/json");

        // Act
        var nul = await FactoryClient.PutAsync(ApiRoutes.Sessions.Draft(session.Id), Body("\\u0000"));
        var surrogate = await FactoryClient.PutAsync(ApiRoutes.Sessions.Draft(session.Id), Body("\\ud83d"));
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        nul.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        surrogate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        read!.RawDraftRevision.ShouldBe(0);
    }

    [Test]
    [Property("Rule", "SES-26")]
    public async Task a_settings_change_with_only_the_zone_and_the_save_time_gives_400()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var partial = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZone = "UTC", savedAt = JournalScenario.Morning });
        var noZone = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { locationCaptureEnabled = false, requirePeopleProposalApproval = true, savedAt = JournalScenario.Morning });

        // Assert
        partial.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        noZone.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    [Property("Rule", "SES-29")]
    public async Task a_rename_reaches_each_session_also_with_words_that_it_cannot_write_again_in_another()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var person = await (await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label = "Old" })).Content.ReadFromJsonAsync<PersonDto>();
        var cut = await FactoryClient.AddSessionAsync();
        var healthy = await FactoryClient.AddSessionAsync();
        var cutWords = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"mention\",\"attrs\":{\"personId\":\"" + person!.Id + "\",\"label\":\"\\ud83d\"}}]}]}";
        var healthyWords = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"mention\",\"attrs\":{\"personId\":\"" + person.Id + "\",\"label\":\"Old\"}}]}]}";
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Cleaned(cut.Id), new { text = cutWords, baseRevision = 0, savedAt = JournalScenario.Morning })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.SaveDraftAsync(healthy.Id, healthyWords, 0)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var rename = await FactoryClient.PutAsJsonAsync(ApiRoutes.People.GetRecord(person.Id), new { label = "New" });
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(healthy.Id));

        // Assert
        rename.StatusCode.ShouldBe(HttpStatusCode.OK);
        read!.RawText.ShouldContain("\"label\":\"New\"");
    }

    [Test]
    [Property("Rule", "SES-8")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task a_replay_from_a_fast_clock_makes_no_revision_and_does_not_undo_a_newer_save(bool raw)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = raw ? ApiRoutes.Sessions.Draft(session.Id) : ApiRoutes.Sessions.Cleaned(session.Id);
        var fast = DateTimeOffset.UtcNow.AddHours(5);
        await FactoryClient.PutAsJsonAsync(route, new { text = "words of the fast phone", baseRevision = 0, savedAt = fast });
        await FactoryClient.PutAsJsonAsync(route, new { text = "newer words", baseRevision = 1, savedAt = DateTimeOffset.UtcNow });

        // Act
        var replay = await FactoryClient.PutAsJsonAsync(route, new { text = "words of the fast phone", baseRevision = 0, savedAt = fast });
        var result = await replay.Content.ReadFromJsonAsync<DraftSaveResultDto>();
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Outcome.ShouldBe("Unchanged");
        result.LatestRevision.ShouldBe(2);
        (raw ? read!.RawText : read!.CleanedText).ShouldBe("newer words");
    }

    [Test]
    [Property("Rule", "SES-26")]
    public async Task a_retry_from_a_fast_clock_does_not_turn_location_capture_on_again()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var fast = DateTimeOffset.UtcNow.AddHours(5);
        var on = new { timeZone = "UTC", locationCaptureEnabled = true, requirePeopleProposalApproval = false, savedAt = fast };
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, on)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var off = new { timeZone = "UTC", locationCaptureEnabled = false, requirePeopleProposalApproval = false, savedAt = fast.AddMinutes(1) };
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, off)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var retry = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, on);
        var created = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = JournalScenario.Morning, latitude = 1.5, longitude = 2.5 });
        var session = await created.Content.ReadFromJsonAsync<SessionDto>();

        // Assert
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        session!.Latitude.ShouldBeNull();
        session.Longitude.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "SES-26")]
    public async Task a_retry_after_more_than_16_newer_fast_changes_does_not_turn_location_capture_on_again()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var fast = DateTimeOffset.UtcNow.AddHours(5);
        var on = new { timeZone = "UTC", locationCaptureEnabled = true, requirePeopleProposalApproval = false, savedAt = fast };
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, on)).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var change in Enumerable.Range(1, 17))
        {
            var off = new { timeZone = "UTC", locationCaptureEnabled = false, requirePeopleProposalApproval = false, savedAt = fast.AddMinutes(change) };
            (await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, off)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Act
        var retry = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, on);
        var created = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = JournalScenario.Morning, latitude = 1.5, longitude = 2.5 });
        var session = await created.Content.ReadFromJsonAsync<SessionDto>();

        // Assert
        (await retry.Content.ReadFromJsonAsync<UserSettingDto>())!.LocationCaptureEnabled.ShouldBeFalse();
        session!.Latitude.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "SES-21")]
    public async Task a_retry_after_more_than_16_newer_fast_changes_does_not_undo_the_metadata()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var fast = DateTimeOffset.UtcNow.AddHours(5);
        var first = new { activity = "Walking", moods = Array.Empty<string>(), userTopics = FirstTopic, savedAt = fast };
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), first)).StatusCode.ShouldBe(HttpStatusCode.OK);
        foreach (var change in Enumerable.Range(1, 17))
        {
            var newer = new { activity = "Resting", moods = Array.Empty<string>(), userTopics = new[] { $"newer {change}" }, savedAt = fast.AddMinutes(change) };
            (await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), newer)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Act
        var retry = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), first);
        var read = await retry.Content.ReadFromJsonAsync<SessionDto>();

        // Assert
        read!.Activity.ShouldBe("Resting");
        read.Topics.ShouldHaveSingleItem().Name.ShouldBe("newer 17");
    }

    [Test]
    [Property("Rule", "SES-21")]
    public async Task a_retry_with_the_stored_time_does_not_undo_the_metadata_also_with_more_digits_than_the_database_keeps()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        const string time = "2026-06-01T08:00:00.1234567Z";
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { activity = "Resting", savedAt = time })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var other = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { activity = "Walking", savedAt = time });

        // Assert
        (await other.Content.ReadFromJsonAsync<SessionDto>())!.Activity.ShouldBe("Resting");
    }

    [Test]
    [Property("Rule", "SES-8")]
    public async Task a_save_of_the_same_words_keeps_the_save_time_so_a_later_offline_edit_keeps_the_slot()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        (await FactoryClient.SaveDraftAsync(session.Id, "words", 0, JournalScenario.Morning)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var resaved = await FactoryClient.SaveDraftAsync(session.Id, "words", 1, JournalScenario.Morning.AddHours(2));

        // Act
        var offline = await FactoryClient.SaveDraftAsync(session.Id, "words from an offline phone", 0, JournalScenario.Morning.AddHours(1));
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        (await resaved.Content.ReadFromJsonAsync<DraftSaveResultDto>())!.SavedAt.ShouldBe(JournalScenario.Morning);
        (await offline.Content.ReadFromJsonAsync<DraftSaveResultDto>())!.Outcome.ShouldBe("Won");
        read!.RawText.ShouldBe("words from an offline phone");
    }

    [Test]
    [Property("Rule", "SES-21")]
    public async Task a_user_topic_that_matches_an_ai_topic_survives_and_shows_once_with_the_spelling_of_the_user()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        await JournalScenario.ExecuteOutOfBandAsync("UPDATE sessions SET ai_topics = '[\"Work\",\"Trips\"]' WHERE id = @id", ("@id", session.Id));

        // Act
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { userTopics = ShoutedTopic, savedAt = JournalScenario.Morning });
        var kept = await change.Content.ReadFromJsonAsync<SessionDto>();
        var removal = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { userTopics = Array.Empty<string>(), savedAt = JournalScenario.Morning.AddMinutes(1) });
        var removed = await removal.Content.ReadFromJsonAsync<SessionDto>();

        // Assert
        kept!.Topics.Select(t => (t.Name, t.Provenance)).ShouldBe([("WORK", "UserSet"), ("Trips", "AiSuggested")]);
        removed!.Topics.Select(t => (t.Name, t.Provenance)).ShouldBe([("Trips", "AiSuggested")]);
    }

    [Test]
    [Property("Rule", "SES-3")]
    public async Task a_create_and_its_replay_give_the_same_text_for_the_start()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var body = new { clientSessionId = Guid.NewGuid().ToString(), startedAt = "2026-05-01T10:00:00.1234567+05:00" };

        // Act
        var created = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, body);
        var replay = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, body);
        var first = await created.Content.ReadFromJsonAsync<JsonElement>();
        var second = await replay.Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.GetProperty("startedAt").GetString().ShouldBe(first.GetProperty("startedAt").GetString());
    }

    [Test]
    public async Task an_explicit_null_list_is_an_empty_list()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();

        // Act
        var metadata = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { moods = (string[]?)null, userTopics = new string?[] { null }, savedAt = JournalScenario.Morning });
        var correction = await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, new { canonicalTerm = "term", mishearings = (string[]?)null });

        // Assert
        metadata.StatusCode.ShouldBe(HttpStatusCode.OK);
        correction.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Test]
    public async Task a_revision_number_that_is_not_a_number_gives_400()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();

        // Act
        var raw = await FactoryClient.GetAsync($"{ApiRoutes.Sessions.RawRevisions(session.Id)}/abc");
        var cleaned = await FactoryClient.GetAsync($"{ApiRoutes.Sessions.CleanedRevisions(session.Id)}/abc");

        // Assert
        raw.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        cleaned.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task a_delete_records_on_its_span_if_it_scrubbed_the_wal()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();

        // Act
        var deleted = await FactoryClient.DeleteAsync(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        AllExportedSpans()
            .Where(s => s.GetTagItem("app.session_id") as string == session.Id)
            .ShouldContain(s => s.GetTagItem("app.session.wal_scrubbed") is bool);
    }

    [Test]
    [Property("Rule", "SES-1")]
    public async Task a_replay_of_the_create_returns_the_one_session()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var clientSessionId = Guid.NewGuid().ToString();
        var first = await FactoryClient.AddSessionAsync(clientSessionId: clientSessionId);

        // Act
        var replay = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId, startedAt = JournalScenario.Morning.AddDays(5) });
        var page = await FactoryClient.GetFromJsonAsync<SessionPageDto>(ApiRoutes.Sessions.Root);

        // Assert
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadFromJsonAsync<SessionDto>())!.Id.ShouldBe(first.Id);
        page!.Items.ShouldHaveSingleItem().Id.ShouldBe(first.Id);
    }

    [Test]
    [Property("Rule", "SES-1")]
    public async Task two_people_can_use_the_same_client_session_id_and_neither_learns_of_the_other()
    {
        // Arrange
        var (admin, member) = await JournalScenario.AddPeopleAsync();
        var clientSessionId = Guid.NewGuid().ToString();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var ofMember = await FactoryClient.AddSessionAsync(clientSessionId: clientSessionId);

        // Act
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var ofAdmin = await FactoryClient.AddSessionAsync(clientSessionId: clientSessionId);

        // Assert
        ofAdmin.Id.ShouldNotBe(ofMember.Id);
    }

    [Test]
    [Property("Rule", "SES-6")]
    public async Task a_save_from_a_stale_base_that_loses_stays_in_the_revisions()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        await FactoryClient.SaveDraftAsync(session.Id, "the newer words", 0, JournalScenario.Morning.AddHours(2));

        // Act
        var lost = await FactoryClient.SaveDraftAsync(session.Id, "the older words", 0, JournalScenario.Morning.AddHours(1));
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));
        var revisions = await FactoryClient.GetFromJsonAsync<RevisionPageDto>(ApiRoutes.Sessions.RawRevisions(session.Id));
        var kept = await FactoryClient.GetFromJsonAsync<RevisionDto>(ApiRoutes.Sessions.RawRevision(session.Id, 2));

        // Assert
        (await lost.Content.ReadFromJsonAsync<DraftSaveResultDto>())!.Outcome.ShouldBe("Lost");
        read!.RawText.ShouldBe("the newer words");
        revisions!.Items.Select(r => r.Number).ShouldBe([2, 1]);
        kept!.Text.ShouldBe("the older words");
    }

    [Test]
    [Property("Rule", "SES-12")]
    public async Task a_deleted_session_is_gone_and_its_replay_gives_409()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        await FactoryClient.SaveDraftAsync(session.Id, "words", 0);

        // Act
        var delete = await FactoryClient.DeleteAsync(ApiRoutes.Sessions.GetRecord(session.Id));
        var read = await FactoryClient.GetAsync(ApiRoutes.Sessions.GetRecord(session.Id));
        var revision = await FactoryClient.GetAsync(ApiRoutes.Sessions.RawRevision(session.Id, 1));
        var deleteAgain = await FactoryClient.DeleteAsync(ApiRoutes.Sessions.GetRecord(session.Id));
        var replay = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { session.ClientSessionId, startedAt = JournalScenario.Morning });

        // Assert
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        revision.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        deleteAgain.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        replay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    [Property("Rule", "SES-18")]
    public async Task a_location_with_capture_off_is_not_stored_and_with_capture_on_it_is()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var location = new { clientSessionId = Guid.NewGuid().ToString(), startedAt = JournalScenario.Morning, latitude = 59.4, longitude = 24.7 };

        // Act
        var off = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, location);
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new
        {
            timeZone = (string?)null,
            locationCaptureEnabled = true,
            requirePeopleProposalApproval = true,
            savedAt = JournalScenario.Morning,
        });
        var on = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, location with { clientSessionId = Guid.NewGuid().ToString() });

        // Assert
        change.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = (await off.Content.ReadFromJsonAsync<SessionDto>())!;
        stored.Latitude.ShouldBeNull();
        stored.Longitude.ShouldBeNull();
        (await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(stored.Id)))!.Latitude.ShouldBeNull();
        (await on.Content.ReadFromJsonAsync<SessionDto>())!.Latitude.ShouldBe(59.4);
    }

    [Test]
    [Property("Rule", "SES-3")]
    public async Task a_change_of_the_time_zone_moves_a_session_to_its_new_day()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync(new DateTimeOffset(2026, 6, 2, 2, 0, 0, TimeSpan.Zero));

        // Act
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new
        {
            timeZone = "America/Chicago",
            locationCaptureEnabled = false,
            requirePeopleProposalApproval = true,
            savedAt = JournalScenario.Morning,
        });
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        session.JournalingDay.ShouldBe(new DateOnly(2026, 6, 2));
        (await change.Content.ReadFromJsonAsync<UserSettingDto>())!.TimeZone.ShouldBe("America/Chicago");
        read!.JournalingDay.ShouldBe(new DateOnly(2026, 6, 1));
    }

    [Test]
    [Property("Rule", "SES-9")]
    public async Task a_rename_of_a_person_changes_each_mention_and_makes_a_revision()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var added = await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label = "Sam" });
        var person = (await added.Content.ReadFromJsonAsync<PersonDto>())!;
        var session = await FactoryClient.AddSessionAsync();
        var mention = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Lunch with \"},"
            + "{\"type\":\"mention\",\"attrs\":{\"personId\":\"" + person.Id + "\",\"label\":\"Sam\"}}]}]}";
        await FactoryClient.SaveDraftAsync(session.Id, mention, 0);

        // Act
        var rename = await FactoryClient.PutAsJsonAsync(ApiRoutes.People.GetRecord(person.Id), new { label = "Samuel" });
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));
        var page = await FactoryClient.GetFromJsonAsync<SessionPageDto>(ApiRoutes.Sessions.Root);
        var first = await FactoryClient.GetFromJsonAsync<RevisionDto>(ApiRoutes.Sessions.RawRevision(session.Id, 1));

        // Assert
        rename.StatusCode.ShouldBe(HttpStatusCode.OK);
        read!.RawText.ShouldContain("\"label\":\"Samuel\"");
        read.RawDraftRevision.ShouldBe(2);
        read.PersonIds.ShouldBe([person.Id]);
        page!.Items.ShouldHaveSingleItem().Preview.ShouldBe("Lunch with Samuel");
        first!.Text.ShouldContain("\"label\":\"Sam\"");
    }

    [Test]
    public async Task a_save_with_no_base_revision_gives_400()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();

        // Act
        var response = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Draft(session.Id), new { text = "words", savedAt = JournalScenario.Morning });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
