namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Tenants.Models;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

public sealed record PasswordPerson(string UserId, string LoginName, string Password);

/// <summary>Arranges a person with a password credential in a tenant of its own, the way a setup or an Admin would leave it.</summary>
public static partial class PasswordPeople
{
    private static readonly PasswordHasher<PasswordCredential> Hasher =
        new(Options.Create(new PasswordHasherOptions { IterationCount = PasswordHashing.IterationCount }));

    public static string HashOf(string password, int iterations = PasswordHashing.IterationCount)
        => new PasswordHasher<PasswordCredential>(Options.Create(new PasswordHasherOptions { IterationCount = iterations })).HashPassword(null!, password);

    /// <param name="issueTemporary">Gives a Temporary credential issued at this time in place of a Chosen one.</param>
    /// <param name="hash">A stored hash in place of the hash of the password.</param>
    /// <param name="wrongTries">Wrong passwords in a row that the credential has already noted.</param>
    /// <param name="sameTenantAs">A person whose tenant the new person joins, in place of a tenant of its own.</param>
    public static async Task<PasswordPerson> AddAsync(
        AuthFlowFactory host, DateTimeOffset? issueTemporary = null, string? hash = null, int wrongTries = 0, string? password = null,
        PasswordPerson? sameTenantAs = null)
    {
        var loginName = $"ada-{Guid.NewGuid():N}";
        password ??= $"a long journal passphrase {Guid.NewGuid():N}";
        var stored = hash ?? Hasher.HashPassword(null!, password);
        var userId = await host.WithDatabaseAsync(async db =>
        {
            var tenantId = sameTenantAs is null
                ? null
                : await db.Users.IgnoreQueryFilters().Where(u => u.Id == sameTenantAs.UserId).Select(u => u.TenantId).SingleAsync();
            var tenant = tenantId is null ? Tenant.Create(new TenantForCreation { Name = $"Tenant {Guid.NewGuid():N}" }) : null;
            var user = User.CreateFromLogin(new NewPerson("Ada", "Lovelace", null, "ada"), tenantId ?? tenant!.Id, UserRole.Admin());
            LoginName.TryOf(loginName, out var name).ShouldBeTrue();
            var credential = issueTemporary is { } issuedAt
                ? PasswordCredential.IssueTemporary(user.Id, name!, stored, issuedAt)
                : PasswordCredential.Choose(user.Id, name!, stored, DateTimeOffset.UtcNow);
            for (var i = 0; i < wrongTries; i++)
                credential.NoteFailure();
            if (tenant is not null)
                db.Tenants.Add(tenant);
            db.Users.Add(user);
            db.Set<PasswordCredential>().Add(credential);
            await db.SaveChangesAsync();
            return user.Id;
        });
        return new PasswordPerson(userId, loginName, password);
    }

    public static Task<PasswordCredential> CredentialOfAsync(AuthFlowFactory host, string userId)
        => host.WithDatabaseAsync(db => db.Set<PasswordCredential>().AsNoTracking().SingleAsync(p => p.UserId == userId));

    /// <summary>Opens a page with a password form and gives the state of its attempt.</summary>
    public static async Task<string> FormStateAsync(this AuthBrowser browser, string url = "/auth/login", string? address = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (address is not null)
            request.Headers.Add(AuthFlowFactory.ConnectionAddressHeader, address);
        var page = await browser.SendAsync(request);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        return StateIn(await page.Content.ReadAsStringAsync());
    }

    public static string StateIn(string html)
    {
        var state = StateField().Match(html).Groups["state"].Value;
        state.ShouldNotBeNullOrEmpty();
        return state;
    }

    public static Task<HttpResponseMessage> PostLoginAsync(this AuthBrowser browser, string state, string loginName, string password, string? address = null)
        => browser.PostAsync("/auth/password/login", [("state", state), ("loginName", loginName), ("password", password)], address);

    public static async Task<HttpResponseMessage> SignInAsync(this AuthBrowser browser, string loginName, string password, string? address = null)
        => await browser.PostLoginAsync(await browser.FormStateAsync(address: address), loginName, password, address);

    public static Task<HttpResponseMessage> PostAsync(this AuthBrowser browser, string url, IEnumerable<(string Name, string Value)> fields, string? address = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(field => KeyValuePair.Create(field.Name, field.Value))),
        };
        if (address is not null)
            request.Headers.Add(AuthFlowFactory.ConnectionAddressHeader, address);
        return browser.SendAsync(request);
    }

    /// <summary>Issues a temporary password through the API, as an Admin in the browser of a web session does.</summary>
    public static Task<HttpResponseMessage> IssueTemporaryAsync(this AuthBrowser browser, PasswordPerson admin, string userId, string? loginName = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/users/{userId}/temporary-password")
        {
            Content = JsonContent.Create(new { loginName }),
        };
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, admin.UserId);
        return browser.SendAsync(request);
    }

    [GeneratedRegex("name=\"state\" value=\"(?<state>[^\"]+)\"")]
    private static partial Regex StateField();
}
