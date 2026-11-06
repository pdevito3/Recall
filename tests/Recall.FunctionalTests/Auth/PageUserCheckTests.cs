namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Recall.FunctionalTests.TestUtilities;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

public class PageUserCheckTests
{
    private static readonly DateTimeOffset Morning = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static string NewSubject() => $"subject-{Guid.NewGuid():N}";

    private static AuthBrowser NewBrowser() => new(AuthHosts.Open);

    private static async Task SwapToAsync(AuthBrowser browser, string subject)
    {
        (await browser.PostFormAsync("/auth/logout", [], csrf: true)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await browser.LoginWebAsync(subject);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? pageUser, object? body = null)
    {
        var request = new HttpRequestMessage(method, $"/{url}");
        request.Headers.Add("X-CSRF", "1");
        if (pageUser is not null)
            request.Headers.Add(PageUserCheck.Header, pageUser);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static HttpRequestMessage Save(string sessionId, string text, string? pageUser, int baseRevision = 0)
        => Request(HttpMethod.Put, ApiRoutes.Sessions.Draft(sessionId), pageUser, new { text, baseRevision, savedAt = Morning.AddMinutes(baseRevision + 1) });

    private static async Task<string> AddSessionAsync(AuthBrowser browser, string pageUser)
    {
        var created = await browser.SendAsync(Request(HttpMethod.Post, ApiRoutes.Sessions.Root, pageUser, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = Morning }));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task ShouldBeTheMismatchAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("type").GetString().ShouldBe(PageUserCheck.MismatchType);
    }

    [Test]
    public async Task the_words_of_a_page_of_the_first_person_never_reach_the_second_person()
    {
        // Arrange
        using var browser = NewBrowser();
        await browser.LoginWebAsync(NewSubject());
        var first = await browser.UserOfSessionAsync();
        var entryOfFirst = await AddSessionAsync(browser, first);
        (await browser.SendAsync(Save(entryOfFirst, "Words of the first person", first))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await SwapToAsync(browser, NewSubject());
        var second = await browser.UserOfSessionAsync();

        // Act
        var saveOnTheOldEntry = await browser.SendAsync(Save(entryOfFirst, "One more word", first, baseRevision: 1));
        var newEntry = await browser.SendAsync(Request(HttpMethod.Post, ApiRoutes.Sessions.Root, first, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = Morning }));
        var read = await browser.SendAsync(Request(HttpMethod.Get, ApiRoutes.Sessions.Root, first));
        var timelineOfSecond = await browser.SendAsync(Request(HttpMethod.Get, ApiRoutes.Sessions.Root, second));
        await SwapToAsync(browser, NewSubject());

        // Assert
        await ShouldBeTheMismatchAsync(saveOnTheOldEntry);
        await ShouldBeTheMismatchAsync(newEntry);
        await ShouldBeTheMismatchAsync(read);
        timelineOfSecond.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await timelineOfSecond.Content.ReadAsStringAsync()).ShouldNotContain(entryOfFirst);
    }

    [Test]
    public async Task the_first_person_finds_the_entry_unchanged_after_the_refusal()
    {
        // Arrange
        var firstSubject = NewSubject();
        using var browser = NewBrowser();
        await browser.LoginWebAsync(firstSubject);
        var first = await browser.UserOfSessionAsync();
        var entry = await AddSessionAsync(browser, first);
        (await browser.SendAsync(Save(entry, "Saved words", first))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await SwapToAsync(browser, NewSubject());

        // Act
        await ShouldBeTheMismatchAsync(await browser.SendAsync(Save(entry, "Words that must not land", first, baseRevision: 1)));
        await SwapToAsync(browser, firstSubject);
        var read = await browser.SendAsync(Request(HttpMethod.Get, ApiRoutes.Sessions.GetRecord(entry), first));

        // Assert
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await read.Content.ReadAsStringAsync()).ShouldContain("Saved words");
    }

    [Test]
    public async Task a_cookie_write_that_names_no_user_gets_the_same_refusal_and_a_read_passes()
    {
        // Arrange
        using var browser = NewBrowser();
        await browser.LoginWebAsync(NewSubject());
        var body = new { clientSessionId = Guid.NewGuid().ToString(), startedAt = Morning };

        // Act
        var write = await browser.SendAsync(Request(HttpMethod.Post, ApiRoutes.Sessions.Root, pageUser: null, body));
        var read = await browser.SendAsync(Request(HttpMethod.Get, ApiRoutes.Sessions.Root, pageUser: null));

        // Assert
        await ShouldBeTheMismatchAsync(write);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    [Arguments("usr_someone_else")]
    [Arguments("")]
    public async Task a_cookie_write_with_another_name_is_refused(string named)
    {
        // Arrange
        using var browser = NewBrowser();
        await browser.LoginWebAsync(NewSubject());

        // Act
        var write = await browser.SendAsync(Request(HttpMethod.Post, ApiRoutes.Sessions.Root, named, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = Morning }));

        // Assert
        await ShouldBeTheMismatchAsync(write);
    }

    [Test]
    public async Task a_cookie_write_with_two_values_of_the_header_is_refused()
    {
        // Arrange
        using var browser = NewBrowser();
        await browser.LoginWebAsync(NewSubject());
        var mine = await browser.UserOfSessionAsync();
        var request = Request(HttpMethod.Post, ApiRoutes.Sessions.Root, pageUser: null, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = Morning });
        request.Headers.TryAddWithoutValidation(PageUserCheck.Header, [mine, "usr_someone_else"]);

        // Act
        var write = await browser.SendAsync(request);

        // Assert
        await ShouldBeTheMismatchAsync(write);
    }

    [Test]
    public async Task a_native_client_with_a_bearer_token_needs_no_header()
    {
        // Arrange
        using var browser = NewBrowser();
        var tokens = await browser.LoginNativeAsync(NewSubject());
        var request = new HttpRequestMessage(HttpMethod.Post, $"/{ApiRoutes.Sessions.Root}")
        {
            Content = JsonContent.Create(new { clientSessionId = Guid.NewGuid().ToString(), startedAt = Morning }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        // Act
        var write = await browser.SendAsync(request);

        // Assert
        write.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Test]
    public async Task the_sign_in_page_for_a_page_of_another_person_does_not_open_the_journal_of_the_person_who_is_signed_in()
    {
        // Arrange
        using var browser = NewBrowser();
        await browser.LoginWebAsync(NewSubject());

        // Act
        var plain = await browser.GetAsync("/auth/login");
        var afterMismatch = await browser.GetAsync("/auth/login?anotherPerson=1");

        // Assert
        plain.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        afterMismatch.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await afterMismatch.Content.ReadAsStringAsync();
        page.ShouldContain(AuthPages.AnotherPersonMessage);
        page.ShouldContain("Sign in with");
    }
}
