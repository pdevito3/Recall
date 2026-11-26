namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Recall.FunctionalTests.TestUtilities;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

public class LastAdminTests
{
    private const string Me = "/auth/me";

    [Test]
    public async Task an_owner_who_deletes_themselves_gets_a_conflict_and_stays_signed_in()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Closed);
        await browser.LoginWebAsync("owner");
        var ownerId = await OwnerIdAsync();

        // Act
        var response = await browser.SendAsync(await AsThePageOfAsync(browser, HttpMethod.Delete, $"/{ApiRoutes.Users.Delete(ownerId)}"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(response)).ShouldBe(LastAdmin.RefusedMessage);
        (await browser.GetAsync(Me, csrf: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task an_owner_who_demotes_themselves_gets_a_conflict_and_stays_an_admin()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Closed);
        await browser.LoginWebAsync("owner");
        var ownerId = await OwnerIdAsync();
        var request = await AsThePageOfAsync(browser, HttpMethod.Delete, $"/{ApiRoutes.Users.RemoveRole(ownerId, "Admin")}");

        // Act
        var response = await browser.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(response)).ShouldBe(LastAdmin.RefusedMessage);
        (await browser.GetAsync($"/{ApiRoutes.Users.GetList}", csrf: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task an_owner_who_removes_their_own_access_to_manage_people_gets_a_conflict()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Closed);
        await browser.LoginWebAsync("owner");
        var ownerId = await OwnerIdAsync();

        // Act
        var response = await browser.SendAsync(await AsThePageOfAsync(browser, HttpMethod.Delete, $"/{ApiRoutes.Users.RemovePermission(ownerId, "manage_user_access")}"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(response)).ShouldBe(LastAdmin.RefusedMessage);
    }

    [Test]
    public async Task an_owner_who_removes_their_own_permission_to_read_people_gets_a_conflict()
    {
        // Arrange
        using var browser = new AuthBrowser(AuthHosts.Closed);
        await browser.LoginWebAsync("owner");
        var ownerId = await OwnerIdAsync();

        // Act
        var response = await browser.SendAsync(await AsThePageOfAsync(browser, HttpMethod.Delete, $"/{ApiRoutes.Users.RemovePermission(ownerId, "read_users")}"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DetailOf(response)).ShouldBe(LastAdmin.RefusedMessage);
    }

    private static Task<string> OwnerIdAsync()
        => AuthHosts.Closed.WithDatabaseAsync(db => db.Set<ExternalLogin>()
            .IgnoreQueryFilters()
            .Where(link => link.Subject == "owner")
            .Select(link => link.UserId)
            .SingleAsync());

    private static async Task<HttpRequestMessage> AsThePageOfAsync(AuthBrowser browser, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add(PageUserCheck.Header, await browser.UserOfSessionAsync());
        return request;
    }

    private static async Task<string?> DetailOf(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("detail").GetString();
    }
}
