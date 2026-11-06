namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

public sealed partial class NoStoreAnswersTests
{
    private const string Logout = "/auth/logout";

    private static async Task<AuthBrowser> SignedInBrowserAsync()
    {
        var browser = new AuthBrowser(AuthHosts.Open);
        await browser.LoginWebAsync($"subject-{Guid.NewGuid():N}");
        return browser;
    }

    private static List<(string Method, string Path)> EachMappedRoute()
        => AuthHosts.Open.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(route => (route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [HttpMethods.Get])
                .Select(method => (Method: method, Path: PathOf(route))))
            .Where(route => route.Method != HttpMethods.Options)
            .Distinct()
            // A logout ends the session of the walk, so it runs last.
            .OrderBy(route => route.Path == Logout)
            .ThenBy(route => route.Path, StringComparer.Ordinal)
            .ToList();

    private static string PathOf(RouteEndpoint route)
        => "/" + RouteValue().Replace(route.RoutePattern.RawText!.Replace("{version:apiVersion}", "1", StringComparison.Ordinal), "unknown").TrimStart('/');

    private static async Task<string?> GapOfAsync(AuthBrowser browser, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-CSRF", "1");
        using var response = await browser.SendAsync(request);
        return response.Headers.CacheControl?.NoStore == true
            ? null
            : $"{method} {path}: {(int)response.StatusCode}, Cache-Control '{response.Headers.CacheControl}'";
    }

    [Test]
    public async Task each_mapped_route_tells_the_browser_to_store_nothing_for_a_visitor_and_for_a_session()
    {
        // Arrange
        var routes = EachMappedRoute();
        using var visitor = new AuthBrowser(AuthHosts.Open);
        using var session = await SignedInBrowserAsync();
        var gaps = new List<string>();

        // Act
        foreach (var browser in new[] { visitor, session })
        {
            foreach (var (method, path) in routes)
            {
                if (await GapOfAsync(browser, method, path) is { } gap)
                    gaps.Add(gap);
            }
        }

        // Assert
        routes.Select(route => route.Path).ShouldContain("/api/v1/sessions");
        routes.Select(route => route.Path).ShouldContain("/auth/me");
        routes.Select(route => route.Path).ShouldContain(path => path.Contains("revisions", StringComparison.Ordinal));
        gaps.ShouldBeEmpty();
    }

    [Test]
    [Arguments("/auth/me")]
    [Arguments("/api/v1/users/me")]
    [Arguments("/api/v1/sessions")]
    public async Task a_private_answer_of_a_session_tells_the_browser_to_store_nothing(string path)
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();

        // Act
        using var answer = await browser.GetAsync(path, csrf: true);

        // Assert
        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        answer.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Test]
    [Arguments("GET", "/api/v1/users", HttpStatusCode.Unauthorized)]
    [Arguments("GET", "/api/nothing", HttpStatusCode.NotFound)]
    [Arguments("GET", "/auth/nothing", HttpStatusCode.NotFound)]
    [Arguments("PATCH", "/api/v1/users", HttpStatusCode.MethodNotAllowed)]
    [Arguments("GET", "/API/V1/USERS", HttpStatusCode.Unauthorized)]
    public async Task an_error_answer_tells_the_browser_to_store_nothing(string method, string path, HttpStatusCode status)
    {
        // Arrange
        using var visitor = new AuthBrowser(AuthHosts.Open);

        // Act
        using var answer = await visitor.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        // Assert
        answer.StatusCode.ShouldBe(status);
        answer.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Test]
    public async Task an_answer_from_the_exception_handler_tells_the_browser_to_store_nothing()
    {
        // Arrange
        using var browser = await SignedInBrowserAsync();

        // Act
        using var answer = await browser.GetAsync("/api/v1/users?pageNumber=not-a-number", csrf: true);

        // Assert
        // The exception handler clears the header of UseNoStoreAnswers and sets its own value, so this test holds the framework to it.
        answer.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        answer.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        answer.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteValue();
}
