namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recall.FunctionalTests.TestUtilities;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Tenants.Models;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

public class LastTenantTests
{
    [Test]
    public async Task the_owner_who_deletes_the_last_tenant_gets_a_conflict_and_can_sign_in_again()
    {
        // Arrange
        var host = AuthHosts.Closed;
        using var browser = new AuthBrowser(host);
        await browser.LoginWebAsync("owner");
        var tenant = await TenantOfAsync(host, "owner");

        // Act
        var response = await DeleteAsync(browser, tenant);
        using var again = new AuthBrowser(host);
        await again.LoginWebAsync("owner");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(response)).ShouldBe(LastTenant.RefusedMessage);
        again.SessionCookie.ShouldNotBeNull();
        (await host.WithDatabaseAsync(db => db.Tenants.CountAsync(t => t.Id == tenant))).ShouldBe(1);
    }

    [Test]
    public async Task a_member_who_holds_only_manage_tenant_gets_a_conflict_on_the_last_tenant()
    {
        // Arrange
        var host = AuthHosts.Open;
        using var owner = new AuthBrowser(host);
        await owner.LoginWebAsync("tenant-owner");
        var subject = $"member-{Guid.NewGuid():N}";
        using var member = new AuthBrowser(host);
        await member.LoginWebAsync(subject);
        var tenant = await TenantOfAsync(host, subject);
        await host.WithDatabaseAsync(async db =>
        {
            var user = await db.Users.Include(u => u.UserPermissions)
                .SingleAsync(u => db.Set<ExternalLogin>().Any(link => link.Subject == subject && link.UserId == u.Id));
            user.Role.Value.ShouldBe(UserRole.User().Value);
            var held = user.UserPermissions.ToList();
            user.AddPermission(Permission.ManageTenant());
            db.UserPermissions.AddRange(user.UserPermissions.Except(held));
            return await db.SaveChangesAsync();
        });

        // Act
        var response = await DeleteAsync(member, tenant);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(response)).ShouldBe(LastTenant.RefusedMessage);
        (await host.WithDatabaseAsync(db => db.Tenants.CountAsync(t => t.Id == tenant))).ShouldBe(1);
    }

    [Test]
    public async Task a_member_of_a_second_tenant_deletes_it_and_then_the_first_tenant_is_the_last()
    {
        // Arrange
        var host = AuthHosts.TwoTenants;
        using var owner = new AuthBrowser(host);
        await owner.LoginWebAsync("first-owner");
        var first = await TenantOfAsync(host, "first-owner");
        var subject = $"second-{Guid.NewGuid():N}";
        var second = await AddTenantWithMemberAsync(host, subject);
        using var member = new AuthBrowser(host);
        await member.LoginWebAsync(subject);

        // Act
        var deleted = await DeleteAsync(member, second);
        var refused = await DeleteAsync(owner, first);

        // Assert
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await host.WithDatabaseAsync(db => db.Tenants.Select(t => t.Id).ToListAsync())).ShouldBe([first]);
        using var again = new AuthBrowser(host);
        await again.LoginWebAsync("first-owner");
        again.SessionCookie.ShouldNotBeNull();
    }

    private static Task<string> AddTenantWithMemberAsync(AuthFlowFactory host, string subject)
        => host.WithDatabaseAsync(async db =>
        {
            var tenant = Tenant.Create(new TenantForCreation { Name = $"Second {subject}" });
            var person = NewPerson.FromClaims(new Profile("Grace", "Hopper", "Grace Hopper", null, subject[..20]));
            var user = User.CreateFromLogin(person, tenant.Id, UserRole.User());
            var held = user.UserPermissions.ToList();
            user.AddPermission(Permission.ManageTenant());
            db.Tenants.Add(tenant);
            db.Users.Add(user);
            db.UserPermissions.AddRange(user.UserPermissions.Except(held));
            var now = TimeProvider.System.GetUtcNow();
            db.Set<ExternalLogin>().Add(new ExternalLogin { Issuer = StubOidcProvider.Issuer, Subject = subject, UserId = user.Id, CreatedAt = now, LastLoginAt = now });
            await db.SaveChangesAsync();
            return tenant.Id;
        });

    private static async Task<HttpResponseMessage> DeleteAsync(AuthBrowser browser, string tenantId)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/{ApiRoutes.Tenants.Delete(tenantId)}");
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await browser.UserOfSessionAsync());
        return await browser.SendAsync(request);
    }

    private static Task<string> TenantOfAsync(AuthFlowFactory host, string subject)
        => host.WithDatabaseAsync(db => db.Users
            .Where(u => db.Set<ExternalLogin>().Any(link => link.Subject == subject && link.UserId == u.Id))
            .Select(u => u.TenantId)
            .SingleAsync());

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("detail").GetString();
    }
}
