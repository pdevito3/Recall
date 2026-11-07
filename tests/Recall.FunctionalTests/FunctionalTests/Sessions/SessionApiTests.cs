namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Content;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.UserSettings.Dtos;
using Serilog.Events;
using Shouldly;
using TestUtilities;

public class SessionApiTests : TestBase
{
    private static readonly string[] FirstTopic = ["first"];
    private static readonly string[] ShoutedTopic = [" WORK "];

    [Test]
    public async Task the_limit_of_a_draft_counts_the_characters_that_a_person_sees_and_not_the_document_around_them()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var document = (int length) => JsonSerializer.Serialize(new
        {
            type = "doc",
            content = Enumerable.Range(0, length / 100).Select(_ => new
            {
                type = "paragraph",
                content = new[] { new { type = "text", text = new string('x', 100), marks = new[] { new { type = "bold" } } } },
            }),
        });

        // Act
        var justUnderTheLimit = await FactoryClient.SaveDraftAsync(session.Id, document(990_000), 0);
        var overTheLimit = await FactoryClient.SaveDraftAsync(session.Id, document(1_000_100), 1);
        var overTheLimitPlain = await FactoryClient.SaveDraftAsync(session.Id, new string('x', 1_000_001), 1);

        // Assert
        document(990_000).Length.ShouldBeGreaterThan(1_000_000);
        justUnderTheLimit.StatusCode.ShouldBe(HttpStatusCode.OK);
        overTheLimit.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await overTheLimit.Content.ReadAsStringAsync()).ShouldContain("1,000,000 characters");
        overTheLimitPlain.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

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
    public async Task a_settings_change_with_only_the_zone_and_the_version_gives_400()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var partial = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZone = "UTC", version = 0 });
        var noZone = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { locationCaptureEnabled = false, requirePeopleProposalApproval = true, version = 0 });

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
    public async Task a_replay_of_a_win_from_a_fast_clock_says_kept_and_makes_no_revision_and_does_not_undo_a_newer_save(bool raw)
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
        result!.Outcome.ShouldBe("Kept");
        result.DraftRevision.ShouldBe(1);
        result.LatestRevision.ShouldBe(2);
        (raw ? read!.RawText : read!.CleanedText).ShouldBe("newer words");
    }

    private static async Task AppendChunkAsync(string sessionId, Recall.Server.Domain.Users.User owner, string words, DateTimeOffset? now = null)
    {
        var services = TestingWebApplicationFactory.Instance.Services;
        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateJournalScope(new JournalOwner(owner.Id, owner.TenantId));
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await dbContext.Sessions.SingleAsync(s => s.Id == sessionId);
        row.TryAppendTranscript(words, now ?? DateTimeOffset.UtcNow).ShouldBeTrue();
        await dbContext.SaveChangesAsync();
    }

    [Test]
    [Property("Rule", "SES-36")]
    public async Task a_replay_of_a_save_that_won_under_a_chunk_says_kept_and_the_draft_keeps_its_words_and_the_chunk()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = ApiRoutes.Sessions.Draft(session.Id);
        var savedAt = DateTimeOffset.UtcNow;
        await FactoryClient.PutAsJsonAsync(route, new { text = "typed words", baseRevision = 0, savedAt });
        await AppendChunkAsync(session.Id, member, "heard words");

        // Act
        var replay = await FactoryClient.PutAsJsonAsync(route, new { text = "typed words", baseRevision = 0, savedAt });
        var result = await replay.Content.ReadFromJsonAsync<DraftSaveResultDto>();
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        result!.Outcome.ShouldBe("Kept");
        result.DraftRevision.ShouldBe(1);
        result.LatestRevision.ShouldBe(2);
        var plain = ProseMirrorToPlainText.Render(read!.RawText);
        plain.ShouldContain("typed words");
        plain.ShouldContain("heard words");
    }

    [Test]
    [Property("Rule", "SES-36")]
    public async Task a_replay_of_a_save_that_lost_under_a_chunk_says_lost()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = ApiRoutes.Sessions.Draft(session.Id);
        var savedAt = DateTimeOffset.UtcNow;
        await AppendChunkAsync(session.Id, member, "heard words");
        var first = await FactoryClient.PutAsJsonAsync(route, new { text = "typed words", baseRevision = 0, savedAt });

        // Act
        var replay = await FactoryClient.PutAsJsonAsync(route, new { text = "typed words", baseRevision = 0, savedAt });
        var result = await replay.Content.ReadFromJsonAsync<DraftSaveResultDto>();

        // Assert
        (await first.Content.ReadFromJsonAsync<DraftSaveResultDto>())!.Outcome.ShouldBe("Lost");
        result!.Outcome.ShouldBe("Lost");
        result.DraftRevision.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "SES-36")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task each_of_two_saves_of_the_same_words_keeps_its_first_answer(bool raw)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = raw ? ApiRoutes.Sessions.Draft(session.Id) : ApiRoutes.Sessions.Cleaned(session.Id);
        var now = DateTimeOffset.UtcNow;
        await FactoryClient.PutAsJsonAsync(route, new { text = "words of the draft", baseRevision = 0, savedAt = now });
        var lost = new { text = "the words", baseRevision = 0, savedAt = now.AddHours(-1) };
        var won = new { text = "the words", baseRevision = 1, savedAt = now.AddMinutes(1) };
        await FactoryClient.PutAsJsonAsync(route, lost);
        await FactoryClient.PutAsJsonAsync(route, won);
        await FactoryClient.PutAsJsonAsync(route, new { text = "newer words", baseRevision = 3, savedAt = now.AddMinutes(2) });

        // Act
        var replayOfTheLoss = await (await FactoryClient.PutAsJsonAsync(route, lost)).Content.ReadFromJsonAsync<DraftSaveResultDto>();
        var replayOfTheWin = await (await FactoryClient.PutAsJsonAsync(route, won)).Content.ReadFromJsonAsync<DraftSaveResultDto>();

        // Assert
        replayOfTheLoss!.Outcome.ShouldBe("Lost");
        replayOfTheLoss.LatestRevision.ShouldBe(4);
        replayOfTheWin!.Outcome.ShouldBe("Kept");
        replayOfTheWin.DraftRevision.ShouldBe(3);
    }

    [Test]
    [Property("Rule", "SES-8")]
    [Arguments(true, 1)]
    [Arguments(false, 1)]
    [Arguments(true, 0)]
    [Arguments(false, 0)]
    public async Task a_replay_of_a_save_that_said_unchanged_is_a_new_save_after_the_draft_moved_and_then_answers_from_its_row(bool raw, int baseOfTheSave)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = raw ? ApiRoutes.Sessions.Draft(session.Id) : ApiRoutes.Sessions.Cleaned(session.Id);
        var now = DateTimeOffset.UtcNow;
        await FactoryClient.PutAsJsonAsync(route, new { text = "same words", baseRevision = 0, savedAt = now.AddMinutes(-3) });
        var same = new { text = "same words", baseRevision = baseOfTheSave, savedAt = now.AddMinutes(-2) };
        var unchanged = await (await FactoryClient.PutAsJsonAsync(route, same)).Content.ReadFromJsonAsync<DraftSaveResultDto>();
        await FactoryClient.PutAsJsonAsync(route, new { text = "same words, and later words", baseRevision = 1, savedAt = now.AddMinutes(-1) });

        // Act
        var replay = await (await FactoryClient.PutAsJsonAsync(route, same)).Content.ReadFromJsonAsync<DraftSaveResultDto>();
        var second = await (await FactoryClient.PutAsJsonAsync(route, same)).Content.ReadFromJsonAsync<DraftSaveResultDto>();
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        unchanged!.Outcome.ShouldBe("Unchanged");
        replay!.Outcome.ShouldBe("Lost");
        replay.DraftRevision.ShouldBeNull();
        replay.LatestRevision.ShouldBe(3);
        second!.Outcome.ShouldBe("Lost");
        second.LatestRevision.ShouldBe(3);
        (raw ? read!.RawText : read!.CleanedText).ShouldBe("same words, and later words");
    }

    [Test]
    [Property("Rule", "SES-5")]
    [Property("Rule", "SES-8")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task words_that_the_draft_took_with_a_new_time_from_an_older_base_lose_and_undo_nothing(bool raw)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = raw ? ApiRoutes.Sessions.Draft(session.Id) : ApiRoutes.Sessions.Cleaned(session.Id);
        var now = DateTimeOffset.UtcNow;
        await FactoryClient.PutAsJsonAsync(route, new { text = "first words", baseRevision = 0, savedAt = now.AddMinutes(-3) });
        await FactoryClient.PutAsJsonAsync(route, new { text = "second words", baseRevision = 1, savedAt = now.AddMinutes(-2) });

        // Act
        var retry = await (await FactoryClient.PutAsJsonAsync(route, new { text = "first words", baseRevision = 0, savedAt = now })).Content.ReadFromJsonAsync<DraftSaveResultDto>();
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        retry!.Outcome.ShouldBe("Lost");
        retry.DraftRevision.ShouldBeNull();
        retry.LatestRevision.ShouldBe(3);
        (raw ? read!.RawText : read!.CleanedText).ShouldBe("second words");
    }

    [Test]
    [Property("Rule", "SES-8")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task words_of_an_older_draft_win_from_the_current_draft_and_the_replay_says_kept_at_that_win(bool raw)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = raw ? ApiRoutes.Sessions.Draft(session.Id) : ApiRoutes.Sessions.Cleaned(session.Id);
        var now = DateTimeOffset.UtcNow;
        await FactoryClient.PutAsJsonAsync(route, new { text = "first words", baseRevision = 0, savedAt = now.AddMinutes(-4) });
        await FactoryClient.PutAsJsonAsync(route, new { text = "second words", baseRevision = 1, savedAt = now.AddMinutes(-3) });
        var restore = new { text = "first words", baseRevision = 2, savedAt = now.AddMinutes(-2) };

        // Act
        var restored = await (await FactoryClient.PutAsJsonAsync(route, restore)).Content.ReadFromJsonAsync<DraftSaveResultDto>();
        await FactoryClient.PutAsJsonAsync(route, new { text = "third words", baseRevision = 3, savedAt = now.AddMinutes(-1) });
        var replay = await (await FactoryClient.PutAsJsonAsync(route, restore)).Content.ReadFromJsonAsync<DraftSaveResultDto>();

        // Assert
        restored!.Outcome.ShouldBe("Won");
        restored.DraftRevision.ShouldBe(3);
        replay!.Outcome.ShouldBe("Kept");
        replay.DraftRevision.ShouldBe(3);
    }

    [Test]
    [Property("Rule", "SES-8")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task a_replay_of_a_loss_says_lost_when_its_base_became_the_number_of_the_draft(bool raw)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = raw ? ApiRoutes.Sessions.Draft(session.Id) : ApiRoutes.Sessions.Cleaned(session.Id);
        var now = DateTimeOffset.UtcNow;
        await FactoryClient.PutAsJsonAsync(route, new { text = "first words", baseRevision = 0, savedAt = now.AddMinutes(-3) });
        var high = new { text = "words from a guess", baseRevision = 3, savedAt = now.AddMinutes(-2) };
        await FactoryClient.PutAsJsonAsync(route, high);
        await FactoryClient.PutAsJsonAsync(route, new { text = "words of the draft", baseRevision = 1, savedAt = now.AddMinutes(-1) });

        // Act
        var replay = await (await FactoryClient.PutAsJsonAsync(route, high)).Content.ReadFromJsonAsync<DraftSaveResultDto>();
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        replay!.Outcome.ShouldBe("Lost");
        replay.LatestRevision.ShouldBe(3);
        (raw ? read!.RawText : read!.CleanedText).ShouldBe("words of the draft");
    }

    [Test]
    [Property("Rule", "SES-5")]
    [Property("Rule", "SES-36")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task a_base_that_names_a_revision_that_lost_or_an_older_draft_loses_at_any_time(bool raw)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = raw ? ApiRoutes.Sessions.Draft(session.Id) : ApiRoutes.Sessions.Cleaned(session.Id);
        var now = DateTimeOffset.UtcNow;
        await FactoryClient.PutAsJsonAsync(route, new { text = "first words", baseRevision = 0, savedAt = now.AddMinutes(-5) });
        var lost = await (await FactoryClient.PutAsJsonAsync(route, new { text = "words that lost", baseRevision = 99, savedAt = now.AddMinutes(-4) })).Content.ReadFromJsonAsync<DraftSaveResultDto>();
        await FactoryClient.PutAsJsonAsync(route, new { text = "merged words", baseRevision = 1, savedAt = now.AddMinutes(-3) });

        // Act
        var fromTheLoss = await (await FactoryClient.PutAsJsonAsync(route, new { text = "from the latest number", baseRevision = lost!.LatestRevision, savedAt = now.AddMinutes(-2) })).Content.ReadFromJsonAsync<DraftSaveResultDto>();
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));
        var fromAnOlderDraft = await (await FactoryClient.PutAsJsonAsync(route, new { text = "from another device", baseRevision = 1, savedAt = now.AddMinutes(-1) })).Content.ReadFromJsonAsync<DraftSaveResultDto>();

        // Assert
        fromTheLoss!.Outcome.ShouldBe("Lost");
        fromTheLoss.DraftRevision.ShouldBeNull();
        (raw ? read!.RawText : read!.CleanedText).ShouldBe("merged words");
        fromAnOlderDraft!.Outcome.ShouldBe("Lost");
    }

    [Test]
    [Property("Rule", "SES-9")]
    public async Task a_rename_moves_the_number_so_a_save_from_before_it_loses_and_a_save_from_it_wins_on_each_route()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var person = (await (await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label = "Sam" })).Content.ReadFromJsonAsync<PersonDto>())!;
        var session = await FactoryClient.AddSessionAsync();
        var mention = $$$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"mention","attrs":{"personId":"{{{person.Id}}}","label":"Sam"}}]}]}""";
        var now = DateTimeOffset.UtcNow;
        string[] routes = [ApiRoutes.Sessions.Draft(session.Id), ApiRoutes.Sessions.Cleaned(session.Id)];
        foreach (var route in routes)
            await FactoryClient.PutAsJsonAsync(route, new { text = mention, baseRevision = 0, savedAt = now.AddMinutes(-3) });
        await FactoryClient.PutAsJsonAsync(ApiRoutes.People.GetRecord(person.Id), new { label = "Samuel" });

        foreach (var route in routes)
        {
            // Act
            var fromBefore = await (await FactoryClient.PutAsJsonAsync(route, new { text = "words from before the rename", baseRevision = 1, savedAt = now.AddHours(2) })).Content.ReadFromJsonAsync<DraftSaveResultDto>();
            var fromTheRename = await (await FactoryClient.PutAsJsonAsync(route, new { text = "words from the rename", baseRevision = 2, savedAt = now.AddMinutes(-1) })).Content.ReadFromJsonAsync<DraftSaveResultDto>();

            // Assert
            fromBefore!.Outcome.ShouldBe("Lost", route);
            fromTheRename!.Outcome.ShouldBe("Won", route);
        }
    }

    [Test]
    [Property("Rule", "SES-5")]
    public async Task a_cleanup_moves_the_number_so_a_save_from_before_it_loses_and_a_save_from_it_wins()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = ApiRoutes.Sessions.Cleaned(session.Id);
        var now = DateTimeOffset.UtcNow;
        var services = TestingWebApplicationFactory.Instance.Services;
        await using (var scope = services.GetRequiredService<IServiceScopeFactory>().CreateJournalScope(new JournalOwner(member.Id, member.TenantId)))
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await dbContext.Sessions.SingleAsync(s => s.Id == session.Id);
            row.BeginCleanup();
            row.CompleteCleanup(new CleanupOutcome("words of the cleanup", "synopsis", [], [], []), 0, now.AddMinutes(-3));
            await dbContext.SaveChangesAsync();
        }

        // Act
        var fromBefore = await (await FactoryClient.PutAsJsonAsync(route, new { text = "words from before the cleanup", baseRevision = 0, savedAt = now.AddHours(2) })).Content.ReadFromJsonAsync<DraftSaveResultDto>();
        var fromTheCleanup = await (await FactoryClient.PutAsJsonAsync(route, new { text = "words from the cleanup", baseRevision = 1, savedAt = now.AddMinutes(-1) })).Content.ReadFromJsonAsync<DraftSaveResultDto>();

        // Assert
        fromBefore!.Outcome.ShouldBe("Lost");
        fromTheCleanup!.Outcome.ShouldBe("Won");
    }

    [Test]
    [Property("Rule", "SES-36")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task words_that_lost_win_from_the_number_of_a_read_with_a_new_time_so_a_client_does_not_lose_them_in_a_loop(bool raw)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = raw ? ApiRoutes.Sessions.Draft(session.Id) : ApiRoutes.Sessions.Cleaned(session.Id);
        var now = DateTimeOffset.UtcNow;
        await FactoryClient.PutAsJsonAsync(route, new { text = "words of the draft", baseRevision = 0, savedAt = now });
        var lost = await FactoryClient.PutAsJsonAsync(route, new { text = "words of the slow phone", baseRevision = 0, savedAt = now.AddHours(-1) });

        // Act
        var sameTime = await FactoryClient.PutAsJsonAsync(route, new { text = "words of the slow phone", baseRevision = 1, savedAt = now.AddHours(-1) });
        var again = await FactoryClient.PutAsJsonAsync(route, new { text = "words of the slow phone", baseRevision = 1, savedAt = now.AddMinutes(-1) });
        var result = await again.Content.ReadFromJsonAsync<DraftSaveResultDto>();
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));

        // Assert
        (await lost.Content.ReadFromJsonAsync<DraftSaveResultDto>())!.Outcome.ShouldBe("Lost");
        (await sameTime.Content.ReadFromJsonAsync<DraftSaveResultDto>())!.Outcome.ShouldBe("Lost");
        result!.Outcome.ShouldBe("Won");
        result.DraftRevision.ShouldBe(3);
        (raw ? read!.RawText : read!.CleanedText).ShouldBe("words of the slow phone");
    }

    [Test]
    [Property("Rule", "SES-8")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task a_replay_of_a_save_that_lost_says_lost_and_a_repeat_of_the_draft_says_unchanged(bool raw)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = raw ? ApiRoutes.Sessions.Draft(session.Id) : ApiRoutes.Sessions.Cleaned(session.Id);
        var now = DateTimeOffset.UtcNow;
        await FactoryClient.PutAsJsonAsync(route, new { text = "words of the Draft", baseRevision = 0, savedAt = now });
        var lost = new { text = "words that lost", baseRevision = 0, savedAt = now.AddHours(-1) };
        var first = await (await FactoryClient.PutAsJsonAsync(route, lost)).Content.ReadFromJsonAsync<DraftSaveResultDto>();

        // Act
        var replay = await (await FactoryClient.PutAsJsonAsync(route, lost)).Content.ReadFromJsonAsync<DraftSaveResultDto>();
        var repeat = await (await FactoryClient.PutAsJsonAsync(route, new { text = "words of the Draft", baseRevision = 1, savedAt = now.AddMinutes(1) })).Content.ReadFromJsonAsync<DraftSaveResultDto>();

        // Assert
        first!.Outcome.ShouldBe("Lost");
        replay!.Outcome.ShouldBe("Lost");
        replay.LatestRevision.ShouldBe(first.LatestRevision);
        repeat!.Outcome.ShouldBe("Unchanged");
        repeat.DraftRevision.ShouldBe(1);
        repeat.LatestRevision.ShouldBe(first.LatestRevision);
    }

    [Test]
    [Property("Rule", "SES-5")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task a_base_that_no_draft_had_changes_no_draft_and_keeps_the_words_as_a_revision(bool raw)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var route = raw ? ApiRoutes.Sessions.Draft(session.Id) : ApiRoutes.Sessions.Cleaned(session.Id);
        await FactoryClient.PutAsJsonAsync(route, new { text = "words of the Draft", baseRevision = 0, savedAt = DateTimeOffset.UtcNow });

        // Act
        var reply = await FactoryClient.PutAsJsonAsync(route, new { text = "words from a guess", baseRevision = 999_999, savedAt = DateTimeOffset.UtcNow });
        var result = await reply.Content.ReadFromJsonAsync<DraftSaveResultDto>();
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));
        var revision = await FactoryClient.GetFromJsonAsync<System.Text.Json.JsonElement>(
            (raw ? ApiRoutes.Sessions.RawRevisions(session.Id) : ApiRoutes.Sessions.CleanedRevisions(session.Id)) + $"/{result!.LatestRevision}");

        // Assert
        reply.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Outcome.ShouldBe("Lost");
        result.DraftRevision.ShouldBeNull();
        (raw ? read!.RawText : read!.CleanedText).ShouldBe("words of the Draft");
        revision.GetProperty("text").GetString().ShouldBe("words from a guess");
    }

    [Test]
    [Property("Rule", "SES-26")]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task a_settings_change_with_no_zone_stores_none_and_replies_with_a_null_zone(string? zone)
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var withZone = new { timeZone = "Europe/Riga", locationCaptureEnabled = false, requirePeopleProposalApproval = false, version = 0 };
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, withZone)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZone = zone, locationCaptureEnabled = false, requirePeopleProposalApproval = false, version = 1 });
        var stored = await FactoryClient.GetFromJsonAsync<UserSettingDto>(ApiRoutes.UserSettings.Root);

        // Assert
        (await change.Content.ReadFromJsonAsync<UserSettingDto>())!.TimeZone.ShouldBeNull();
        stored!.TimeZone.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "SES-21")]
    public async Task a_metadata_body_with_only_topics_clears_the_activity_and_the_moods()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var full = new { activity = "Walking", moods = new[] { "Calm" }, userTopics = FirstTopic, metadataVersion = 0 };
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), full)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { userTopics = FirstTopic, metadataVersion = 1 });
        var read = await change.Content.ReadFromJsonAsync<SessionDto>();

        // Assert
        read!.Activity.ShouldBe("None");
        read.Moods.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-21")]
    public async Task a_metadata_body_that_sends_topics_in_place_of_user_topics_empties_the_user_topics()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var full = new { activity = "Walking", moods = Array.Empty<string>(), userTopics = FirstTopic, metadataVersion = 0 };
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), full)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { topics = FirstTopic, metadataVersion = 1 });
        var read = await change.Content.ReadFromJsonAsync<SessionDto>();

        // Assert
        read!.Topics.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-26")]
    public async Task the_log_says_if_a_settings_change_applied_and_holds_no_zone()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var change = new { timeZone = "Pacific/Chatham", locationCaptureEnabled = false, requirePeopleProposalApproval = false, version = 0 };

        // Act
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, change)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, change)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert
        var logs = TestingWebApplicationFactory.Instance.CapturedLogs;
        AppliedValuesOf(logs.Holding(member.Id)).ShouldBe(["True", "False"]);
        logs.Holding("Pacific/Chatham").ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-21")]
    public async Task the_log_says_if_a_metadata_change_applied_and_holds_no_topic()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var topic = $"topic{Guid.NewGuid():N}";
        var change = new { activity = "Walking", moods = Array.Empty<string>(), userTopics = new[] { topic }, metadataVersion = 0 };

        // Act
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), change)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), change)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Assert
        var logs = TestingWebApplicationFactory.Instance.CapturedLogs;
        AppliedValuesOf(logs.Holding(session.Id)).ShouldBe(["True", "False"]);
        logs.Holding(topic).ShouldBeEmpty();
    }

    private static List<string> AppliedValuesOf(IEnumerable<LogEvent> events)
        => events.Where(e => e.Properties.ContainsKey("Applied")).Select(e => e.Properties["Applied"].ToString()).ToList();

    [Test]
    [Property("Rule", "SES-8")]
    public async Task a_save_of_the_same_words_keeps_the_save_time_and_makes_no_revision()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        (await FactoryClient.SaveDraftAsync(session.Id, "words", 0, JournalScenario.Morning)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var resaved = await (await FactoryClient.SaveDraftAsync(session.Id, "words", 1, JournalScenario.Morning.AddHours(2))).Content.ReadFromJsonAsync<DraftSaveResultDto>();

        // Assert
        resaved!.Outcome.ShouldBe("Unchanged");
        resaved.SavedAt.ShouldBe(JournalScenario.Morning);
        resaved.LatestRevision.ShouldBe(1);
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
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { userTopics = ShoutedTopic, metadataVersion = 0 });
        var kept = await change.Content.ReadFromJsonAsync<SessionDto>();
        var removal = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { userTopics = Array.Empty<string>(), metadataVersion = 1 });
        var removed = await removal.Content.ReadFromJsonAsync<SessionDto>();

        // Assert
        kept!.Topics.Select(t => (t.Name, t.Provenance)).ShouldBe([("WORK", "UserSet"), ("Trips", "AiSuggested")]);
        removed!.Topics.Select(t => (t.Name, t.Provenance)).ShouldBe([("Trips", "AiSuggested")]);
    }

    [Test]
    [Property("Rule", "SES-21")]
    public async Task a_stored_ai_topic_with_outer_spaces_matches_a_user_topic()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        await JournalScenario.ExecuteOutOfBandAsync("UPDATE sessions SET ai_topics = '[\" Work \",\"Trips\"]' WHERE id = @id", ("@id", session.Id));

        // Act
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { userTopics = ShoutedTopic, metadataVersion = 0 });
        var kept = await change.Content.ReadFromJsonAsync<SessionDto>();

        // Assert
        kept!.Topics.Select(t => (t.Name, t.Provenance)).ShouldBe([("WORK", "UserSet"), ("Trips", "AiSuggested")]);
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
        var metadata = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { moods = (string[]?)null, userTopics = new string?[] { null }, metadataVersion = 0 });
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
            version = 0,
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
            version = 0,
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
