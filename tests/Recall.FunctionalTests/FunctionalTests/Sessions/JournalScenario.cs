namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sync.Dtos;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.EntityGraphs;
using Shouldly;
using TestUtilities;

internal static class JournalScenario
{
    public static readonly DateTimeOffset Morning = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    public static async Task<(User Admin, User Member)> AddPeopleAsync()
    {
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUsers(out var users, u => u.Role = "Admin", u => u.Role = "User");
        await TestBase.InsertAsync(tenant);
        await TestBase.InsertAsync(users);
        return (users[0], users[1]);
    }

    /// <summary>Makes a state that no route can make, on a connection outside the context, as a restore or a later job does.</summary>
    public static async Task ExecuteOutOfBandAsync(string sql, params (string Name, object Value)[] parameters)
    {
        var connectionString = await TestBase.ExecuteDbContextAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> CountOutOfBandAsync(string sql, params (string Name, object Value)[] parameters)
    {
        var connectionString = await TestBase.ExecuteDbContextAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<SessionDto> AddSessionAsync(this HttpClient client, DateTimeOffset? startedAt = null, string? clientSessionId = null)
    {
        var response = await client.PostAsJsonAsync(ApiRoutes.Sessions.Root, new
        {
            clientSessionId = clientSessionId ?? Guid.NewGuid().ToString(),
            startedAt = startedAt ?? Morning,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<SessionDto>()).ShouldNotBeNull();
    }

    public static async Task<HttpResponseMessage> SaveDraftAsync(
        this HttpClient client, string sessionId, string text, int baseRevision, DateTimeOffset? savedAt = null)
        => await client.PutAsJsonAsync(ApiRoutes.Sessions.Draft(sessionId), new { text, baseRevision, savedAt = savedAt ?? Morning.AddMinutes(1) });

    public static async Task<SyncChangePageDto> PullAsync(this HttpClient client, string? cursor = null, int? pageSize = null)
    {
        var query = new List<string>();
        if (cursor is not null)
            query.Add($"cursor={cursor}");
        if (pageSize is not null)
            query.Add($"pageSize={pageSize}");
        var response = await client.GetAsync($"{ApiRoutes.Sync.Changes}?{string.Join('&', query)}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SyncChangePageDto>()).ShouldNotBeNull();
    }
}
