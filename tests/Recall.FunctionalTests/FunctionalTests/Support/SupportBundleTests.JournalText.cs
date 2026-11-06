namespace Recall.FunctionalTests.FunctionalTests.Support;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Recall.FunctionalTests.Auth;
using Recall.Server;
using Recall.Server.Domain.Corrections;
using Recall.Server.Domain.Corrections.Dtos;
using Recall.Server.Domain.People;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;
using TestUtilities;

public partial class SupportBundleTests
{
    private static readonly DateTimeOffset Morning = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static string NewMarker() => $"PRIVATEWORDS{Guid.NewGuid():N}";

    private static async Task<SessionDto> AddSessionAsync(AuthBrowser owner)
    {
        using var added = await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = Morning });
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        return (await added.Content.ReadFromJsonAsync<SessionDto>()).ShouldNotBeNull();
    }

    private static async Task<PersonDto> AddPersonAsync(AuthBrowser owner, string label)
    {
        using var added = await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.People.Root, new { label });
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        return (await added.Content.ReadFromJsonAsync<PersonDto>()).ShouldNotBeNull();
    }

    private static async Task<CorrectionDto> AddCorrectionAsync(AuthBrowser owner, string term, string heard)
    {
        using var added = await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.Corrections.Root, new { canonicalTerm = term, mishearings = new[] { heard } });
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        return (await added.Content.ReadFromJsonAsync<CorrectionDto>()).ShouldNotBeNull();
    }

    private static Task<HttpResponseMessage> SaveDraftAsync(AuthBrowser owner, string route, string text, int baseRevision, int minute) =>
        SendJsonAsync(owner, HttpMethod.Put, route, new { text, baseRevision, savedAt = Morning.AddMinutes(minute) });

    private static async Task<HttpResponseMessage> DeleteAsync(AuthBrowser owner, string route)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/{route}");
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await owner.UserOfSessionAsync());
        return await owner.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendBodyAsync(AuthBrowser owner, HttpMethod method, string route, string body)
    {
        var request = new HttpRequestMessage(method, $"/{route}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await owner.UserOfSessionAsync());
        return await owner.SendAsync(request);
    }

    private static HttpStatusCode[] StatusesOf(IEnumerable<HttpResponseMessage> responses) => [.. responses.Select(response => response.StatusCode)];

    private static string DocumentWith(params object[] blocks) => JsonSerializer.Serialize(new { type = "doc", content = blocks });

    private static object Paragraph(string text) => new { type = "paragraph", content = new object[] { new { type = "text", text } } };

    private static object Mention(string personId, string label) => new { type = "mention", attrs = new { personId, label } };

    /// <summary>Empty when retention removed the file after the list of the folder.</summary>
    private static string ReadShared(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return string.Empty;
        }
    }

    private static (Dictionary<string, string> Logs, Dictionary<string, string> Telemetry) FilesOfHost()
    {
        var configuration = Host.Services.GetRequiredService<IConfiguration>();
        return (FilesIn(OwnerOnlyFiles.LogsDirectory(configuration)!), FilesIn(OwnerOnlyFiles.TelemetryDirectory(configuration)!));

        static Dictionary<string, string> FilesIn(string folder) =>
            Directory.Exists(folder) ? Directory.GetFiles(folder).ToDictionary(file => file, ReadShared) : [];
    }

    /// <summary>
    /// Each sink keeps its order, so a text that came last proves that each earlier text had its chance to arrive. Without this,
    /// a search would pass on a file that has not had the line yet.
    /// </summary>
    private static async Task WaitUntilEachSinkHoldsAsync(string text)
    {
        var missing = "each sink";
        for (var attempt = 0; attempt < 150; attempt++)
        {
            Host.Services.GetRequiredService<TracerProvider>().ForceFlush(1000);
            var (logs, telemetry) = FilesOfHost();
            (string Name, bool Holds)[] sinks =
            [
                ("the log files", logs.Values.Any(content => content.Contains(text, StringComparison.Ordinal))),
                ("the trace files", telemetry.Any(file => Path.GetFileName(file.Key).StartsWith("traces-", StringComparison.Ordinal) && file.Value.Contains(text, StringComparison.Ordinal))),
                ("the OTLP logs", AuthHosts.BundleExport.LogsHold(text)),
                ("the OTLP traces", AuthHosts.BundleExport.TracesHold(text)),
            ];
            missing = string.Join(", ", sinks.Where(sink => !sink.Holds).Select(sink => sink.Name));
            if (missing.Length == 0)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new InvalidOperationException($"{text} did not reach {missing} in 30 seconds.");
    }

    private static void ShouldHoldNoMarker(string marker, Dictionary<string, string> bundle)
    {
        foreach (var (name, content) in bundle)
            content.ShouldNotContain(marker, customMessage: name);

        var (logs, telemetry) = FilesOfHost();
        logs.ShouldNotBeEmpty();
        telemetry.ShouldNotBeEmpty();
        foreach (var (file, content) in logs.Concat(telemetry))
            content.ShouldNotContain(marker, customMessage: file);

        AuthHosts.BundleExport.Holds(marker).ShouldBeFalse("The OTLP export holds the marker.");
        Host.Logs.Holding(marker).ShouldBeEmpty();
        TestingWebApplicationFactory.Instance.CapturedLogs.Holding(marker).ShouldBeEmpty();
    }

    /// <summary>Makes each save of a journal table fail in the database with <paramref name="fault"/> as the message of the exception.</summary>
    private static async Task WhileEachJournalSaveFailsAsync(string fault, Func<Task> act)
    {
        (string Table, string Action)[] saves =
        [
            ("raw_revisions", "INSERT"), ("cleaned_revisions", "INSERT"), ("sessions", "INSERT"), ("sessions", "UPDATE"),
            ("corrections", "INSERT"), ("corrections", "UPDATE"), ("people", "INSERT"), ("people", "UPDATE"),
        ];
        string TriggerOf((string Table, string Action) save) => $"test_fault_{save.Table}_{save.Action}";

        try
        {
            foreach (var save in saves)
                await RunSqlAsync($"CREATE TRIGGER {TriggerOf(save)} BEFORE {save.Action} ON {save.Table} BEGIN SELECT RAISE(ABORT, '{fault}'); END;");

            await act();
        }
        finally
        {
            foreach (var save in saves)
                await RunSqlAsync($"DROP TRIGGER IF EXISTS {TriggerOf(save)};");
        }

        // The app refuses raw SQL on an audited table, so this connection does not go through its context.
        static async Task RunSqlAsync(string sql)
        {
            var connectionString = await Host.WithDatabaseAsync(database => Task.FromResult(database.Database.GetConnectionString()));
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    [Test]
    [Property("Rule", "SES-19")]
    public async Task raw_and_cleaned_reach_no_file_no_otlp_export_and_no_bundle_after_writes_edits_a_correction_and_failed_saves()
    {
        // Arrange
        using var owner = await OwnerAsync();
        var marker = NewMarker();
        var fault = $"FORCEDFAULT{Guid.NewGuid():N}";
        var session = await AddSessionAsync(owner);
        var draft = ApiRoutes.Sessions.Draft(session.Id);
        var cleaned = ApiRoutes.Sessions.Cleaned(session.Id);
        var person = await AddPersonAsync(owner, $"Sam {marker}");

        // Act
        var saves = new List<HttpResponseMessage>
        {
            await SaveDraftAsync(owner, draft, $"raw {marker}", 0, 1),
            await SaveDraftAsync(owner, draft, DocumentWith(Paragraph($"edited raw {marker}"), Mention(person.Id, $"Sam {marker}")), 1, 2),
            await SaveDraftAsync(owner, draft, $"a raw save that loses {marker}", 0, -5),
            await SaveDraftAsync(owner, cleaned, $"cleaned {marker}", 0, 1),
            await SaveDraftAsync(owner, cleaned, DocumentWith(Paragraph($"edited cleaned {marker}"), Mention(person.Id, $"Sam {marker}")), 1, 2),
            await SendJsonAsync(owner, HttpMethod.Put, ApiRoutes.Sessions.Metadata(session.Id), new { activity = marker, moods = new[] { marker }, userTopics = new[] { $"topic {marker}" }, metadataVersion = 0 }),
            await SendJsonAsync(owner, HttpMethod.Put, ApiRoutes.People.GetRecord(person.Id), new { label = $"Samuel {marker}" }),
        };
        var correction = await AddCorrectionAsync(owner, $"term {marker}", $"heard {marker}");
        saves.Add(await SendJsonAsync(owner, HttpMethod.Put, ApiRoutes.Corrections.GetRecord(correction.Id), new { canonicalTerm = $"changed {marker}", mishearings = new[] { $"misheard {marker}" }, version = correction.Version }));
        using var rawRevision = await owner.GetAsync($"/{ApiRoutes.Sessions.RawRevision(session.Id, 2)}", csrf: true);
        using var cleanedRevision = await owner.GetAsync($"/{ApiRoutes.Sessions.CleanedRevision(session.Id, 2)}", csrf: true);
        var reads = new List<HttpResponseMessage>();
        foreach (var route in new[]
        {
            ApiRoutes.Sessions.Root, ApiRoutes.Sessions.GetRecord(session.Id), ApiRoutes.Sessions.RawRevisions(session.Id), ApiRoutes.Sessions.CleanedRevisions(session.Id),
            ApiRoutes.People.Root, ApiRoutes.People.GetRecord(person.Id), ApiRoutes.Corrections.Root, ApiRoutes.Corrections.GetRecord(correction.Id),
        })
            reads.Add(await owner.GetAsync($"/{route}", csrf: true));

        using var rawTooLong = await SaveDraftAsync(owner, draft, marker + new string('x', Session.DraftMaxLength + 1), 3, 3);
        using var cleanedTooLong = await SaveDraftAsync(owner, cleaned, marker + new string('x', Session.DraftMaxLength + 1), 3, 3);
        HttpStatusCode rawFailed = default, cleanedFailed = default;
        await WhileEachJournalSaveFailsAsync(fault, async () =>
        {
            using var rawFault = await SaveDraftAsync(owner, draft, $"a raw save that fails {marker}", 3, 4);
            using var cleanedFault = await SaveDraftAsync(owner, cleaned, $"a cleaned save that fails {marker}", 3, 4);
            (rawFailed, cleanedFailed) = (rawFault.StatusCode, cleanedFault.StatusCode);
        });
        using var deletedCorrection = await DeleteAsync(owner, ApiRoutes.Corrections.GetRecord(correction.Id));
        using var deletedSession = await DeleteAsync(owner, ApiRoutes.Sessions.GetRecord(session.Id));
        var last = await AddSessionAsync(owner);
        await WaitUntilEachSinkHoldsAsync(last.Id);
        await WaitUntilEachSinkHoldsAsync(fault);
        var entries = await EntriesOfBundleAsync(owner);

        // Assert
        StatusesOf(saves).ShouldBe(Enumerable.Repeat(HttpStatusCode.OK, saves.Count));
        StatusesOf(reads).ShouldBe(Enumerable.Repeat(HttpStatusCode.OK, reads.Count));
        (await rawRevision.Content.ReadAsStringAsync()).ShouldContain(marker);
        (await cleanedRevision.Content.ReadAsStringAsync()).ShouldContain(marker);
        rawTooLong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        cleanedTooLong.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        rawFailed.ShouldBe(HttpStatusCode.InternalServerError);
        cleanedFailed.ShouldBe(HttpStatusCode.InternalServerError);
        deletedCorrection.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        deletedSession.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        entries.Where(entry => entry.Key.StartsWith("logs/", StringComparison.Ordinal)).ShouldContain(entry => entry.Value.Contains(last.Id, StringComparison.Ordinal));
        entries.Where(entry => entry.Key.StartsWith("telemetry/traces-", StringComparison.Ordinal)).ShouldContain(entry => entry.Value.Contains(last.Id, StringComparison.Ordinal));
        ShouldHoldNoMarker(marker, entries);
    }

    [Test]
    [Property("Rule", "SES-19")]
    public async Task an_exception_on_a_journal_path_brings_no_journal_text_to_a_log_a_span_or_a_file()
    {
        // Arrange
        using var owner = await OwnerAsync();
        var marker = NewMarker();
        var fault = $"FORCEDFAULT{Guid.NewGuid():N}";
        var session = await AddSessionAsync(owner);
        var draft = ApiRoutes.Sessions.Draft(session.Id);
        var cleaned = ApiRoutes.Sessions.Cleaned(session.Id);
        var person = await AddPersonAsync(owner, "Sam");
        var correction = await AddCorrectionAsync(owner, "term", "heard");
        var correctionRoute = ApiRoutes.Corrections.GetRecord(correction.Id);
        var tooLong = marker + new string('x', Session.DraftMaxLength + 1);
        var deep = string.Concat(Enumerable.Repeat("""{"type":"doc","content":[""", 100)) + marker;

        // A reader of the JSON throws on each of these words, and the message of the throw can name them.
        string[] wordsThatFailToRead =
        [
            $$"""{"type":"doc","{{marker}}":1,"{{marker}}":2}""",
            $$$"""{"type":"doc","content":[{"type":"mention","attrs":{"personId":"{{{person.Id}}}","label":"{{{marker}}}"}},{"type":"paragraph","{{{marker}}}":1,"{{{marker}}}":2}]}""",
            $$"""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"\ud83d {{marker}}"}]}]}""",
            $$"""{"type":"doc","content":[{{marker}}""",
            deep,
            $"{{ {marker}",
        ];

        // Act
        var kept = new List<HttpResponseMessage>();
        var revision = 0;
        foreach (var words in wordsThatFailToRead)
        {
            revision++;
            kept.Add(await SaveDraftAsync(owner, draft, words, revision - 1, revision));
            kept.Add(await SaveDraftAsync(owner, cleaned, words, revision - 1, revision));
        }

        kept.Add(await SendJsonAsync(owner, HttpMethod.Put, ApiRoutes.People.GetRecord(person.Id), new { label = $"Samuel {marker}" }));

        // Each of these bodies stops at the JSON reader of the endpoint.
        var notRead = new List<HttpResponseMessage>
        {
            await SendBodyAsync(owner, HttpMethod.Put, draft, $$"""{"text":"{{marker}}"""),
            await SendBodyAsync(owner, HttpMethod.Put, draft, $$"""{"{{marker}}":["""),
            await SendBodyAsync(owner, HttpMethod.Put, draft, $$"""{"text":"x","{{marker}}":}"""),
            await SendBodyAsync(owner, HttpMethod.Post, ApiRoutes.Corrections.Root, $$"""{"canonicalTerm":"t","{{marker}}":{"""),
            await SendBodyAsync(owner, HttpMethod.Put, draft, $$"""{"text":{{marker}},"baseRevision":0,"savedAt":"2026-06-01T08:00:00Z"}"""),
            await SendBodyAsync(owner, HttpMethod.Put, draft, $$"""{"text":{"{{marker}}":1},"baseRevision":0,"savedAt":"2026-06-01T08:00:00Z"}"""),
            await SendBodyAsync(owner, HttpMethod.Put, draft, $$"""{"text":"\ud83d{{marker}}","baseRevision":0,"savedAt":"2026-06-01T08:00:00Z"}"""),
            await SendBodyAsync(owner, HttpMethod.Put, draft, $$"""{"text":"{{marker}}\q","baseRevision":0,"savedAt":"2026-06-01T08:00:00Z"}"""),
            await SendBodyAsync(owner, HttpMethod.Put, draft, $$"""{"text":"x","baseRevision":"{{marker}}","savedAt":"{{marker}}"}"""),
            await SendBodyAsync(owner, HttpMethod.Put, cleaned, $$"""{"text":"{{marker}}","savedAt":"{{marker}}"}"""),
            await SendBodyAsync(owner, HttpMethod.Post, ApiRoutes.Corrections.Root, $$"""{"canonicalTerm":"t","mishearings":"{{marker}}"}"""),
            await SendBodyAsync(owner, HttpMethod.Post, ApiRoutes.People.Root, $$"""{"label":["{{marker}}"]}"""),
        };

        // Each of these breaks a rule of the domain, and the message of each rule goes to the caller.
        var refused = new List<HttpResponseMessage>
        {
            await SaveDraftAsync(owner, draft, tooLong, revision, revision + 1),
            await SaveDraftAsync(owner, cleaned, tooLong, revision, revision + 1),
            await SaveDraftAsync(owner, draft, $"\u0000{marker}", revision, revision + 1),
            await SaveDraftAsync(owner, cleaned, $"\u0000{marker}", revision, revision + 1),
            await SendJsonAsync(owner, HttpMethod.Put, ApiRoutes.Sessions.Metadata(session.Id), new { activity = marker + new string('x', Session.MetadataValueMaxLength + 1), moods = Array.Empty<string>(), userTopics = Array.Empty<string>(), metadataVersion = 0 }),
            await SendJsonAsync(owner, HttpMethod.Put, ApiRoutes.Sessions.Metadata(session.Id), new { activity = "None", moods = new[] { $"\u0000{marker}" }, userTopics = Array.Empty<string>(), metadataVersion = 0 }),
            await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.Corrections.Root, new { canonicalTerm = marker + new string('x', Correction.CanonicalTermMaxLength + 1), mishearings = Array.Empty<string>() }),
            await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.Corrections.Root, new { canonicalTerm = "t", mishearings = new[] { marker + new string('x', Correction.MishearingMaxLength + 1) } }),
            await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.People.Root, new { label = marker + new string('x', Person.LabelMaxLength + 1) }),
        };
        using var stale = await SendJsonAsync(owner, HttpMethod.Put, correctionRoute, new { canonicalTerm = marker, mishearings = new[] { marker }, version = correction.Version + 99 });

        // Each of these fails in the database, so the server logs the exception, and the span records it.
        var failed = new List<HttpResponseMessage>();
        await WhileEachJournalSaveFailsAsync(fault, async () =>
        {
            failed.Add(await SaveDraftAsync(owner, draft, $"raw {marker}", revision, revision + 1));
            failed.Add(await SaveDraftAsync(owner, cleaned, $"cleaned {marker}", revision, revision + 1));
            failed.Add(await SendJsonAsync(owner, HttpMethod.Put, ApiRoutes.Sessions.Metadata(session.Id), new { activity = marker, moods = new[] { marker }, userTopics = new[] { marker }, metadataVersion = 0 }));
            failed.Add(await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.Sessions.Root, new { clientSessionId = marker, startedAt = Morning }));
            failed.Add(await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.Corrections.Root, new { canonicalTerm = marker, mishearings = new[] { marker } }));
            failed.Add(await SendJsonAsync(owner, HttpMethod.Put, correctionRoute, new { canonicalTerm = marker, mishearings = new[] { marker }, version = correction.Version }));
            failed.Add(await SendJsonAsync(owner, HttpMethod.Post, ApiRoutes.People.Root, new { label = marker }));
            failed.Add(await SendJsonAsync(owner, HttpMethod.Put, ApiRoutes.People.GetRecord(person.Id), new { label = marker }));
        });
        await WaitUntilEachSinkHoldsAsync((await AddSessionAsync(owner)).Id);
        await WaitUntilEachSinkHoldsAsync(fault);
        var entries = await EntriesOfBundleAsync(owner);

        // Assert
        StatusesOf(kept).ShouldBe(Enumerable.Repeat(HttpStatusCode.OK, kept.Count));
        StatusesOf(notRead).ShouldBe(Enumerable.Repeat(HttpStatusCode.BadRequest, notRead.Count));
        StatusesOf(refused).ShouldBe(Enumerable.Repeat(HttpStatusCode.UnprocessableEntity, refused.Count));
        foreach (var refusal in refused)
            (await refusal.Content.ReadAsStringAsync()).ShouldNotContain(marker);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await stale.Content.ReadAsStringAsync()).ShouldNotContain(marker);
        StatusesOf(failed).ShouldBe(Enumerable.Repeat(HttpStatusCode.InternalServerError, failed.Count));
        Host.Logs.Holding(fault).Where(line => line.Exception is not null).Select(line => line.TraceId).Distinct().Count().ShouldBe(failed.Count);
        ShouldHoldNoMarker(marker, entries);
    }
}
