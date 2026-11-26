namespace Recall.FunctionalTests.Auth;

using Recall.SharedTestHelpers.Fakes.User;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.PersonalAccessTokens;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Tenants.Models;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints;

public sealed record PatPerson(string Subject, string UserId, string TenantId);

/// <summary>People who sign in at the stub provider, in a tenant of their own, and their personal access tokens.</summary>
public static class PatPeople
{
    public static async Task<(PatPerson Admin, PatPerson Member)> AddTenantAsync(AuthFlowFactory host)
    {
        var tenantId = await host.WithDatabaseAsync(async db =>
        {
            var tenant = Tenant.Create(new TenantForCreation { Name = $"Tokens {Guid.NewGuid():N}" });
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            return tenant.Id;
        });
        return (await AddPersonAsync(host, tenantId, [UserRole.Admin()]), await AddPersonAsync(host, tenantId, []));
    }

    public static Task<PatPerson> AddPersonAsync(AuthFlowFactory host, string tenantId, IReadOnlyList<UserRole> roles, params Permission[] permissions)
        => host.WithDatabaseAsync(async db =>
        {
            var subject = $"pat-{Guid.NewGuid():N}";
            var user = User.CreateFromLogin(NewPerson.FromClaims(new Profile("Ada", "Lovelace", "Ada Lovelace", null, subject[..20])), tenantId, roles);
            user.AddPermissions(permissions);
            db.Users.Add(user);
            var now = TimeProvider.System.GetUtcNow();
            db.Set<ExternalLogin>().Add(new ExternalLogin { Issuer = StubOidcProvider.Issuer, Subject = subject, UserId = user.Id, CreatedAt = now, LastLoginAt = now });
            await db.SaveChangesAsync();
            return new PatPerson(subject, user.Id, tenantId);
        });

    public static Task<NewPersonalAccessToken> StartTokenAsync(
        AuthFlowFactory host, PatPerson person, TokenGrant grant, DateTimeOffset? expiresAt = null, string name = "Laptop")
        => host.WithDatabaseAsync(async db =>
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == person.UserId);
            return await host.WithStoreAsync(store => store.StartPersonalAccessTokenAsync(
                user, PersonalAccessTokenName.Of(name), grant, expiresAt ?? DateTimeOffset.UtcNow.AddDays(30), CancellationToken.None));
        });

    public static HttpClient ClientWith(AuthFlowFactory host, string value)
    {
        var client = host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", value);
        return client;
    }

    public static Task<McpClient> McpAsync(HttpClient client)
        => McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(client.BaseAddress!, EntrypointRegistration.McpPath),
                    TransportMode = HttpTransportMode.StreamableHttp,
                },
                client,
                ownsHttpClient: false),
            new McpClientOptions { InitializationTimeout = TimeSpan.FromMinutes(1) });

    public static Task<AuthSession> RowAsync(AuthFlowFactory host, string tokenId)
        => host.WithDatabaseAsync(db => db.Set<AuthSession>().AsNoTracking().SingleAsync(s => s.Id == tokenId));
}
