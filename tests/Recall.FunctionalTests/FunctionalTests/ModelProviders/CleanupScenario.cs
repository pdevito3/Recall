namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Net;
using System.Net.Http.Json;
using BackWave.Jobs;
using BackWave.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.FakeModelProvider;
using Recall.FunctionalTests.FunctionalTests.Sessions;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.ModelProviders.Dtos;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Jobs;
using Recall.Server.Domain.Users;
using Shouldly;
using TestUtilities;

/// <summary>A tenant with an Admin who connected a model provider, and a member with a journal, against a fake that speaks the wire format.</summary>
internal sealed class CleanupScenario : IAsyncDisposable
{
    private CleanupScenario(FakeModelProvider fake, string apiKey, User admin, User member, ModelProviderDto provider, HttpClient client)
    {
        Fake = fake;
        ApiKey = apiKey;
        Admin = admin;
        Member = member;
        Provider = provider;
        Client = client;
    }

    public FakeModelProvider Fake { get; }

    public string ApiKey { get; }

    public User Admin { get; }

    public User Member { get; }

    public ModelProviderDto Provider { get; }

    public HttpClient Client { get; }

    private static IServiceProvider Services => TestingWebApplicationFactory.Instance.Services;

    public static string NewKey() => $"fake-key-{Guid.NewGuid():N}";

    public static string BaseUrlOf(FakeModelProvider fake, ModelProviderKind kind)
        => kind == ModelProviderKind.Anthropic ? fake.AnthropicBaseUrl : fake.CompatibleBaseUrl;

    public static async Task<CleanupScenario> StartAsync(HttpClient client, ModelProviderKind kind = ModelProviderKind.Anthropic, bool pickModel = true)
    {
        var apiKey = NewKey();
        var fake = await FakeModelProvider.StartAsync(apiKey);
        var (admin, member) = await JournalScenario.AddPeopleAsync();
        client.SignInAsAppUser(admin.Identifier);
        var provider = await ConnectAsync(client, kind, BaseUrlOf(fake, kind), apiKey);
        if (pickModel)
            provider = await PickModelAsync(client, provider, FakeModelProvider.ModelId);
        client.SignInAsAppUser(member.Identifier);
        return new CleanupScenario(fake, apiKey, admin, member, provider, client);
    }

    public static async Task<ModelProviderDto> ConnectAsync(HttpClient client, ModelProviderKind kind, string? baseUrl, string? apiKey, string name = "My provider")
    {
        var added = await client.PostAsJsonAsync(ApiRoutes.ModelProviders.Root, new { kind = kind.ToString(), name, baseUrl, apiKey });
        added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        return (await added.Content.ReadFromJsonAsync<ModelProviderDto>()).ShouldNotBeNull();
    }

    public static async Task<ModelProviderDto> PickModelAsync(HttpClient client, ModelProviderDto provider, string model)
    {
        var updated = await client.PutAsJsonAsync(ApiRoutes.ModelProviders.GetRecord(provider.Id), new
        {
            name = provider.Name,
            baseUrl = provider.BaseUrl,
            model,
            makeActive = true,
            version = provider.Version,
        });
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        return (await updated.Content.ReadFromJsonAsync<ModelProviderDto>()).ShouldNotBeNull();
    }

    /// <summary>A Session that holds these words as Raw, and whose Cleanup job waits in the queue.</summary>
    public async Task<SessionDto> SessionWithRunningCleanupAsync(string words)
    {
        var session = await SessionWithWordsAsync(words);
        var started = await Client.PostAsync(ApiRoutes.Sessions.Cleanup(session.Id), null);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        return (await started.Content.ReadFromJsonAsync<SessionDto>()).ShouldNotBeNull();
    }

    public async Task<SessionDto> SessionWithWordsAsync(string words)
    {
        var session = await Client.AddSessionAsync();
        var saved = await Client.SaveDraftAsync(session.Id, words, 0);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        return session;
    }

    public async Task<SessionDto> ReadAsync(string sessionId)
    {
        var read = await Client.GetAsync(ApiRoutes.Sessions.GetRecord(sessionId));
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await read.Content.ReadFromJsonAsync<SessionDto>()).ShouldNotBeNull();
    }

    public static Task<string> RunIdOfAsync(string sessionId) => TestBase.ExecuteDbContextAsync(db => db.Sessions
        .IgnoreQueryFilters()
        .Where(s => s.Id == sessionId)
        .Select(s => s.CleanupRunId!)
        .FirstAsync());

    public static Task<string> TenantIdOfAsync(string sessionId) => TestBase.ExecuteDbContextAsync(db => db.Sessions
        .IgnoreQueryFilters()
        .Where(s => s.Id == sessionId)
        .Select(s => s.TenantId)
        .FirstAsync());

    /// <summary>Runs the job the way that a worker does, with one claim.</summary>
    public async Task RunJobAsync(string sessionId, int attempt = 1, string? runId = null, IJobStore? store = null, TimeProvider? clock = null, CancellationToken cancellationToken = default)
    {
        var parts = new List<object>();
        if (store is not null)
            parts.Add(store);
        if (clock is not null)
            parts.Add(clock);
        var job = ActivatorUtilities.CreateInstance<CleanupJob>(Services, [.. parts]);
        await job.CleanupSessionAsync(
            sessionId,
            Member.Id,
            await TenantIdOfAsync(sessionId),
            runId ?? await RunIdOfAsync(sessionId),
            new JobContext { JobId = Guid.NewGuid(), Attempt = attempt },
            cancellationToken);
    }

    public static Task<int> CleanedRevisionCountAsync(string sessionId) => TestBase.ExecuteDbContextAsync(db => db.Set<CleanedRevision>()
        .IgnoreQueryFilters()
        .CountAsync(revision => revision.SessionId == sessionId));

    public static async Task SetProtectedKeyAsync(string providerId, string protectedKey)
        => await JournalScenario.ExecuteOutOfBandAsync("UPDATE model_providers SET protected_api_key = $key WHERE id = $id", ("$key", protectedKey), ("$id", providerId));

    public static string ProtectedKeyUnderAnotherRing()
    {
        var folder = Directory.CreateTempSubdirectory("recall-other-ring");
        var protector = new ApiKeyProtector(DataProtectionProvider.Create(folder));
        return protector.Protect(NewKey())!;
    }

    /// <summary>Each text column of each row, read outside the context, so a search sees what a person with the file sees.</summary>
    public static async Task<string> DumpOfTableAsync(string table)
    {
        var connectionString = await TestBase.ExecuteDbContextAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table}";
        await using var reader = await command.ExecuteReaderAsync();
        var dump = new System.Text.StringBuilder();
        while (await reader.ReadAsync())
        {
            for (var column = 0; column < reader.FieldCount; column++)
            {
                dump.Append(reader.GetValue(column) switch
                {
                    byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                    var value => value?.ToString(),
                }).Append('|');
            }
        }

        return dump.ToString();
    }

    public async ValueTask DisposeAsync() => await Fake.DisposeAsync();
}
