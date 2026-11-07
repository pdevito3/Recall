namespace Recall.UnitTests.Databases;

using System.Globalization;
using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Domain.Agents;
using Recall.Server.Domain.AuditEntries;
using Recall.Server.Domain.Corrections;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.People;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Metadata;
using Recall.Server.Domain.Summaries;
using Recall.Server.Domain.Sync;
using Recall.Server.Domain.Users;
using Recall.Server.Exceptions;
using Recall.SharedTestHelpers.EntityGraphs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

public class StringColumnLengthTests
{
    public sealed record CallerColumn(string RequestField, Func<string, int, string> ValueOfLength, Action<string> Write);

    private static readonly string[] Characters = ["a", "\U0001F600"];

    private static string Text(string character, int length) => string.Concat(Enumerable.Repeat(character, length));

    private const string ProviderAddress = "http://a/";

    private static string AddressOfLength(string character, int length) => ProviderAddress + Text(character, length - ProviderAddress.Length);

    private static string Email(string character, int length) => Text(character, length - "@example.com".Length) + "@example.com";

    private static readonly Dictionary<string, CallerColumn> CallerColumns = new()
    {
        ["users.first_name"] = new("firstName", Text, v => TestGraph.Start(out _).AddTenant(out _).AddUser(out _, u => u.FirstName = v)),
        ["users.last_name"] = new("lastName", Text, v => TestGraph.Start(out _).AddTenant(out _).AddUser(out _, u => u.LastName = v)),
        ["users.identifier"] = new("identifier", Text, v => TestGraph.Start(out _).AddTenant(out _).AddUser(out _, u => u.Identifier = v)),
        ["users.username"] = new("username", Text, v => TestGraph.Start(out _).AddTenant(out _).AddUser(out _, u => u.Username = v)),
        ["users.email"] = new("email", Email, v => TestGraph.Start(out _).AddTenant(out _).AddUser(out _, u => u.Email = v)),
        ["tenants.name"] = new("name", Text, v => TestGraph.Start(out _).AddTenant(out _, t => t.Name = v)),
        ["agents.name"] = new("name", Text, v => TestGraph.Start(out _).AddTenant(out _).AddAgent(out _, a => a.Name = v)),
        ["agents.client_id"] = new("clientId", Text, v => TestGraph.Start(out _).AddTenant(out _).AddAgent(out _, a => a.ClientId = v)),
        ["people.label"] = new("label", Text, v => Person.Create("ten_1", "usr_1", v)),
        ["corrections.canonical_term"] = new("canonicalTerm", Text, v => Correction.Create("ten_1", "usr_1", v, [], false)),
        ["sessions.client_session_id"] = new("clientSessionId", Text, v => Session.Create("ten_1", "usr_1", v, DateTimeOffset.UnixEpoch, null, DateTimeOffset.UnixEpoch)),
        ["sessions.activity"] = new("activity", Text, v => SessionMetadata.Of(v, [], [])),
        ["sessions.raw_text"] = new("text", Text, v => DraftSave.Of(v, 0, DateTimeOffset.UnixEpoch)),
        ["sessions.cleaned_text"] = new("text", Text, v => DraftSave.Of(v, 0, DateTimeOffset.UnixEpoch)),
        ["raw_revisions.text"] = new("text", Text, v => DraftSave.Of(v, 0, DateTimeOffset.UnixEpoch)),
        ["cleaned_revisions.text"] = new("text", Text, v => DraftSave.Of(v, 0, DateTimeOffset.UnixEpoch)),
        ["model_providers.name"] = new("name", Text, v => ModelProvider.Connect("ten_1", ModelProviderKind.OpenAiCompatible, v, ProviderAddress, null)),
        ["model_providers.base_url"] = new("baseUrl", AddressOfLength, v => ModelProvider.Connect("ten_1", ModelProviderKind.OpenAiCompatible, "Mine", v, null)),
        ["model_providers.model"] = new("model", Text, v =>
        {
            var provider = ModelProvider.Connect("ten_1", ModelProviderKind.OpenAiCompatible, "Mine", ProviderAddress, null);
            provider.Update("Mine", ProviderAddress, null, v, provider.Version);
        }),
        ["recordings.client_recording_id"] = new("clientRecordingId", Text, v => Recording.Create("ten_1", "usr_1", "ses_1", v, RecordingFormat.Webm)),
        // new-feature:caller-columns
    };

    private static readonly Dictionary<string, IReadOnlyList<string>> FixedOptionColumns = new()
    {
        ["users.role"] = UserRole.ListNames(),
        ["model_providers.kind"] = Enum.GetNames<ModelProviderKind>(),
        ["sessions.cleanup_failure"] = Enum.GetNames<CleanupFailure>(),
        ["agents.mode"] = [AgentMode.Delegated().Value, AgentMode.Machine().Value],
        ["audit_entries.kind"] = Enum.GetNames<AuditEntryKind>(),
        ["user_permissions.permission"] = Permission.GetAll().Select(p => p.Value).ToList(),
        ["agent_permissions.permission"] = Permission.GetAll().Select(p => p.Value).ToList(),
        ["auth_sessions.client_kind"] = SessionKindText.All,
        ["auth_sessions.revoked_reason"] = RevokeReasonText.All,
        ["auth_login_attempts.client_kind"] = SessionKindText.All,
        ["sessions.cleanup_run"] = Enum.GetNames<CleanupRunState>(),
        ["sync_changes.entity_type"] = Enum.GetNames<SyncEntityType>(),
        ["user_settings.time_zone"] = TimeZoneInfo.GetSystemTimeZones().Where(zone => zone.HasIanaId).Select(zone => zone.Id).ToList(),
        ["recordings.format"] = Enum.GetNames<RecordingFormat>(),
        ["recordings.engine"] = Enum.GetNames<TranscriptionEngineKind>(),
        ["recordings.status"] = Enum.GetNames<RecordingStatus>(),
        ["recordings.problem"] = Enum.GetNames<RecordingProblem>(),
        ["summaries.period"] = Enum.GetNames<SummaryPeriod>(),
        ["summaries.run_state"] = Enum.GetNames<SummaryRunState>(),
        ["summaries.failure"] = Enum.GetNames<SummaryFailure>(),
        // new-feature:fixed-option-columns
    };

    private static readonly HashSet<string> ServerWrittenColumns =
    [
        "recording_slices.sha256",
        "cleaned_revisions.device_result_hash",
        "people.proposal_key",
        "audit_entries.action",
        "audit_entries.actor_agent_id",
        "audit_entries.actor_user_id",
        "audit_entries.entity_id",
        "audit_entries.entity_type",
        "audit_entries.request_name",
        "audit_entries.root_id",
        "audit_entries.root_type",
        "audit_entries.tenant_id",
        "audit_entries.trace_id",
        "auth_app_codes.challenge",
        "auth_app_codes.client_state",
        "auth_app_codes.login_mode",
        "auth_app_codes.redirect_uri",
        "auth_login_attempts.binding_hash",
        "auth_login_attempts.mode_id",
        "auth_login_attempts.native_challenge",
        "auth_login_attempts.native_redirect",
        "auth_login_attempts.native_state",
        "auth_login_attempts.nonce",
        "auth_login_attempts.pkce_verifier",
        "auth_login_attempts.redirect_uri",
        "auth_login_attempts.return_path",
        "auth_sessions.access_hash",
        "auth_sessions.login_mode",
        "auth_sessions.refresh_family_hash",
        "auth_sessions.refresh_hash",
        "corrections.mishearings",
        "key_ring_markers.protected_value",
        "model_providers.protected_api_key",
        "sessions.ai_topics",
        "sessions.mentioned_person_ids",
        "sessions.cleanup_run_id",
        "sessions.mood_suggestions",
        "sessions.moods",
        "sessions.people_proposals",
        "sessions.topic_suggestions",
        "sessions.preview",
        "sessions.synopsis",
        "sessions.user_topics",
        "summaries.content",
        "summaries.run_id",
        "summaries.source_fingerprint",
        "sync_changes.client_session_id",
        "sync_changes.entity_id",
        "sync_changes.user_id",
    ];

    [Test]
    public void every_string_column_is_a_caller_a_fixed_option_or_a_server_column()
    {
        // Arrange
        var columns = NonIdStringColumnsWithMaxLength();

        // Act
        var unclassified = columns.Keys
            .Where(c => !CallerColumns.ContainsKey(c) && !FixedOptionColumns.ContainsKey(c) && !ServerWrittenColumns.Contains(c))
            .Order()
            .ToList();
        var stale = CallerColumns.Keys.Concat(FixedOptionColumns.Keys).Concat(ServerWrittenColumns)
            .Where(c => !columns.ContainsKey(c))
            .Order()
            .ToList();

        // Assert
        unclassified.ShouldBeEmpty("Add each column to CallerColumns with its domain write, to FixedOptionColumns, or to ServerWrittenColumns.");
        stale.ShouldBeEmpty();
    }

    public static IEnumerable<(string, string)> CallerColumnsAndCharacters =>
        CallerColumns.Keys.SelectMany(column => Characters.Select(character => (column, character)));

    [Test]
    [MethodDataSource(nameof(CallerColumnsAndCharacters))]
    public void the_domain_takes_a_value_of_the_column_length_and_refuses_a_longer_one(string column, string character)
    {
        // Arrange
        var maxLength = NonIdStringColumnsWithMaxLength()[column];
        var callerColumn = CallerColumns[column];
        var fits = callerColumn.ValueOfLength(character, maxLength);
        var tooLong = callerColumn.ValueOfLength(character, maxLength + 1);

        // Act
        var writeFits = () => callerColumn.Write(fits);
        var writeTooLong = () => callerColumn.Write(tooLong);

        // Assert
        writeFits.ShouldNotThrow();
        var exception = writeTooLong.ShouldThrow<ValidationException>();
        exception.Property.ShouldBe(callerColumn.RequestField);
        exception.Message.ShouldContain(maxLength.ToString(CultureInfo.InvariantCulture));
    }

    public static IEnumerable<string> CallerColumnNames => CallerColumns.Keys;

    [Test]
    [MethodDataSource(nameof(CallerColumnNames))]
    public void the_domain_refuses_a_too_long_value_of_lone_surrogates_with_a_validation_error(string column)
    {
        // Arrange
        var maxLength = NonIdStringColumnsWithMaxLength()[column];
        var callerColumn = CallerColumns[column];
        var tooLong = callerColumn.ValueOfLength("\uD800", maxLength + 1);

        // Act
        var writeTooLong = () => callerColumn.Write(tooLong);

        // Assert
        writeTooLong.ShouldThrow<ValidationException>().Property.ShouldBe(callerColumn.RequestField);
    }

    private static readonly string[] CharactersNotStorable = ["\0", "\uD800", "\uDC00"];

    public static IEnumerable<(string, string)> CallerColumnsAndCharactersNotStorable =>
        CallerColumns.Keys.SelectMany(column => CharactersNotStorable.Select(character => (column, character)));

    [Test]
    [MethodDataSource(nameof(CallerColumnsAndCharactersNotStorable))]
    public void the_domain_refuses_a_character_that_postgres_cannot_store_with_a_validation_error(string column, string character)
    {
        // Arrange
        var maxLength = NonIdStringColumnsWithMaxLength()[column];
        var callerColumn = CallerColumns[column];
        // The value fits the column, so only the character can make the domain refuse it
        var value = callerColumn.ValueOfLength("a", Math.Min(20, maxLength) - 1).Insert(1, character);

        // Act
        var write = () => callerColumn.Write(value);

        // Assert
        var exception = write.ShouldThrow<ValidationException>();
        exception.Property.ShouldBe(callerColumn.RequestField);
        exception.Message.ShouldContain("a character that is not allowed");
    }

    [Test]
    public void each_option_of_a_fixed_option_column_fits_the_column()
    {
        // Arrange
        var columns = NonIdStringColumnsWithMaxLength();

        // Act
        var tooLong = FixedOptionColumns
            .SelectMany(c => c.Value.Where(o => o.Length > columns[c.Key]).Select(o => $"{c.Key}: {o}"))
            .ToList();

        // Assert
        tooLong.ShouldBeEmpty();
    }

    private static Dictionary<string, int> NonIdStringColumnsWithMaxLength()
    {
        using var context = new AppDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        return model.GetEntityTypes()
            .Where(e => !e.IsOwned())
            .SelectMany(e => e.GetFlattenedProperties())
            .Where(p => (p.GetTypeMapping().Converter?.ProviderClrType ?? p.ClrType) == typeof(string) && p.GetMaxLength() is not null && !p.IsKey() && !p.IsForeignKey())
            .ToDictionary(
                p => $"{p.DeclaringType.ContainingEntityType.GetTableName()}.{p.GetColumnName()}",
                p => p.GetMaxLength()!.Value);
    }
}
