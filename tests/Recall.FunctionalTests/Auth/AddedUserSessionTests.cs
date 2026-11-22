namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

public class AddedUserSessionTests
{
    private static AuthFlowFactory Host => AuthHosts.Password;

    private static string NewAddress() => $"2001:db8:{Random.Shared.Next(1, 0xffff):x}:{Random.Shared.Next(1, 0xffff):x}::1";

    [Test]
    public async Task a_person_that_an_admin_adds_is_the_signed_in_person_after_choosing_a_password()
    {
        // Arrange
        var admin = await PasswordPeople.AddAsync(Host);
        using var adminBrowser = new AuthBrowser(Host);
        (await adminBrowser.SignInAsync(admin.LoginName, admin.Password, NewAddress())).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var add = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users")
        {
            Content = JsonContent.Create(new { firstName = "Grace", lastName = "Hopper", username = "grace", role = "User" }),
        };
        add.Headers.Add("X-CSRF", "1");
        add.Headers.Add(PageUserCheck.Header, admin.UserId);
        var added = await adminBrowser.SendAsync(add);
        var addedBody = await added.Content.ReadAsStringAsync();
        added.StatusCode.ShouldBe(HttpStatusCode.Created, addedBody);
        var userId = JsonDocument.Parse(addedBody).RootElement.GetProperty("id").GetString()!;

        var loginName = $"grace-{Guid.NewGuid():N}";
        var issue = await adminBrowser.IssueTemporaryAsync(admin, userId, loginName);
        issue.StatusCode.ShouldBe(HttpStatusCode.OK, await issue.Content.ReadAsStringAsync());
        var temporary = (await issue.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("password").GetString()!;

        using var browser = new AuthBrowser(Host);
        var choosePage = await browser.SignInAsync(loginName, temporary, NewAddress());
        var choice = await browser.PostAsync("/auth/password/choose",
            [("state", PasswordPeople.StateIn(await choosePage.Content.ReadAsStringAsync())), ("password", $"chosen journal words {Guid.NewGuid():N}")]);
        choice.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // Act
        var tokens = await browser.GetAsync("/api/v1/personal-access-tokens", csrf: true);

        // Assert
        tokens.StatusCode.ShouldBe(HttpStatusCode.OK, await tokens.Content.ReadAsStringAsync());
    }
}
