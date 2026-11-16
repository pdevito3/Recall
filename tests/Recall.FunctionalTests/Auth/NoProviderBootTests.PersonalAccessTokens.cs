namespace Recall.FunctionalTests.Auth;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using Recall.Server.Auth;
using Recall.Server.Domain;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Shouldly;

public partial class NoProviderBootTests
{
    [Test]
    [Property("Rule", "PAT-18")]
    public async Task with_no_provider_a_token_made_earlier_lists_and_calls_the_tools_of_its_grant_on_mcp()
    {
        // Arrange
        var root = Path.Combine(Path.GetTempPath(), $"recall_boot_{Guid.NewGuid():N}");
        var migrating = await ServerProcess.StartAsync(root);
        var database = migrating.Database;
        await migrating.KillAsync();
        var tenantId = EntityId.New(Tenant.IdPrefix);
        var userId = EntityId.New(User.IdPrefix);
        var value = NewPersonalAccessToken.Prefix + Secrets.New();
        var revoked = NewPersonalAccessToken.Prefix + Secrets.New();
        await SeedTokensAsync(database, tenantId, userId, value, revoked);

        // Act
        await using var server = await ServerProcess.StartAsync(root);
        using var client = new HttpClient { BaseAddress = server.Client.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", value);
        await using var mcp = await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri(client.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
                client,
                ownsHttpClient: false));
        var tools = (await mcp.ListToolsAsync()).Select(tool => tool.Name).ToList();
        var tenant = await mcp.CallToolAsync("get_tenant", new Dictionary<string, object?> { ["id"] = tenantId });
        using var revokedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tenants");
        revokedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", revoked);
        using var revokedAnswer = await server.Client.SendAsync(revokedRequest);

        // Assert
        tools.ShouldContain("get_tenant");
        tools.ShouldContain("update_tenant");
        tools.ShouldNotContain("add_tenant");
        tenant.IsError.ShouldNotBe(true);
        tenant.StructuredContent!.Value.GetProperty("id").GetString().ShouldBe(tenantId);
        revokedAnswer.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task SeedTokensAsync(string database, string tenantId, string userId, string value, string revoked)
    {
        var now = DateTimeOffset.UtcNow;
        static string Text(DateTimeOffset at) => at.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tenants (id, name, is_deleted, version) VALUES ($tenant, 'Kept', 0, 1);
            INSERT INTO users (id, tenant_id, first_name, identifier, username, role, is_deleted, version)
            VALUES ($user, $tenant, 'Ada', $user, 'ada', 'Admin', 0, 1);
            INSERT INTO user_permissions (id, user_id, permission, is_deleted, version)
            VALUES ($permission, $user, 'manage_tenant', 0, 1);
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at, name, "grant")
            VALUES ($token, $user, 'pat', 'pat', $hash, $never, $never, $now, 'Laptop', 'none:manage_tenant');
            INSERT INTO auth_sessions (id, user_id, client_kind, login_mode, access_hash, access_expires_at, expires_at, created_at, name, "grant", revoked_at, revoked_reason)
            VALUES ($revokedToken, $user, 'pat', 'pat', $revokedHash, $never, $never, $now, 'Old', 'none:manage_tenant', $now, 'token_revoked');
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$permission", EntityId.New("uperm"));
        command.Parameters.AddWithValue("$token", EntityId.New(PatClaims.IdPrefix));
        command.Parameters.AddWithValue("$hash", TokenHash.Of(value).Hex);
        command.Parameters.AddWithValue("$revokedToken", EntityId.New(PatClaims.IdPrefix));
        command.Parameters.AddWithValue("$revokedHash", TokenHash.Of(revoked).Hex);
        command.Parameters.AddWithValue("$never", Text(SessionPolicy.PersonalAccessTokenNever));
        command.Parameters.AddWithValue("$now", Text(now));
        await command.ExecuteNonQueryAsync();
    }
}
