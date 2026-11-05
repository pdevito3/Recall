namespace Recall.Bff.Tests.Integration;

using System.Net;
using Duende.AccessTokenManagement.OpenIdConnect;
using Recall.Bff.Authentication;
using Recall.Bff.Sessions;
using Recall.Bff.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

public class RefreshLockTests
{
    internal const string SessionWrite = "UPDATE \"sessions\"";
    private const int ParallelCalls = 8;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // Longer than Timeout, so that only the fake clock moves the refresh timers during a test, and a timer on the
    // real clock fails the test
    internal static readonly TimeSpan ClockedTokenRequestTimeout = TimeSpan.FromSeconds(20);

    [Test]
    public async Task parallel_calls_make_one_refresh_and_all_get_the_new_token()
    {
        // Arrange
        await using var app = BffApp.StartAnotherInstance();
        var provider = app.IdentityProvider;
        var subject = NewSubject();
        var (cookie, key) = await SignInAsync(app, $"test_sub={subject}&test_expires_in=60&test_refresh_expires_in=3600");
        var gate = provider.HoldRefreshes(subject);
        using var browser = Cookieless(app);

        // Act
        var calls = Enumerable.Range(0, ParallelCalls).Select(_ => browser.SendAsync(ApiRequest(cookie))).ToList();
        await gate.Arrived.Task.WaitAsync(Timeout);
        await WaitForInProcessWaiterAsync(app);
        gate.Released.SetResult();
        var responses = await Task.WhenAll(calls).WaitAsync(Timeout);

        // Assert
        provider.RefreshCount(subject).ShouldBe(1);
        var issued = provider.IssuedAccessTokens(subject);
        issued.Count.ShouldBe(2);
        var newAccessToken = issued[1];
        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.ReadEchoAsync()).Headers["Authorization"].ShouldBe($"Bearer {newAccessToken}");
        }

        var tokens = await StoredTokensAsync(app, key);
        tokens.AccessToken.ShouldBe(newAccessToken);
        tokens.RefreshToken.ShouldBe(provider.IssuedRefreshTokens(subject)[1]);
    }

    [Test]
    public async Task an_invalid_grant_ends_the_session_and_the_waiter_sends_no_second_refresh()
    {
        // Arrange
        await using var app = BffApp.StartAnotherInstance();
        var provider = app.IdentityProvider;
        var subject = NewSubject();
        var (cookie, key) = await SignInAsync(app, $"test_sub={subject}&test_expires_in=60&test_refresh=fail");
        var gate = provider.HoldRefreshes(subject);
        using var browser = Cookieless(app);

        // Act
        var firstCall = browser.SendAsync(ApiRequest(cookie));
        await gate.Arrived.Task.WaitAsync(Timeout);
        var secondCall = browser.SendAsync(ApiRequest(cookie));
        await WaitForInProcessWaiterAsync(app);
        gate.Released.SetResult();
        var responses = await Task.WhenAll(firstCall, secondCall).WaitAsync(Timeout);

        // Assert
        provider.RefreshCount(subject).ShouldBe(1);
        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.ReadEchoAsync()).Headers.ShouldNotContainKey("Authorization");
            BffApp.SessionCookieValue(response).ShouldBe("");
        }

        (await app.WithStoreAsync(store => store.GetAsync(key, CancellationToken.None))).ShouldBeNull();
    }

    [Test]
    public async Task a_call_that_read_the_old_tokens_before_another_refresh_ended_uses_the_tokens_in_the_row()
    {
        // Arrange
        var reread = new PauseAtCommand("bff_sessions.get", fromCommand: 2);
        await using var app = BffApp.StartAnotherInstance(services: services =>
            services.ConfigureDbContext<BffDbContext>(options => options.AddInterceptors(reread)));
        await using var refresher = BffApp.StartAnotherInstance();
        var provider = app.IdentityProvider;
        var subject = NewSubject();
        var (cookie, key) = await SignInAsync(app, $"test_sub={subject}&test_expires_in=60&test_refresh_expires_in=3600");
        reread.Arm();
        using var slow = Cookieless(app);
        using var fast = Cookieless(refresher);

        // Act
        var slowCall = slow.SendAsync(ApiRequest(cookie));
        await reread.Arrived.WaitAsync(Timeout);
        (await fast.SendAsync(ApiRequest(cookie))).StatusCode.ShouldBe(HttpStatusCode.OK);
        reread.Release();
        var response = await slowCall.WaitAsync(Timeout);

        // Assert
        provider.RefreshCount(subject).ShouldBe(1);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadEchoAsync()).Headers["Authorization"].ShouldBe($"Bearer {provider.IssuedAccessTokens(subject)[1]}");
        (await StoredTokensAsync(app, key)).AccessToken.ShouldBe(provider.IssuedAccessTokens(subject)[1]);
    }

    [Test]
    public async Task two_calls_on_one_host_share_one_refresh_in_process()
    {
        // Arrange
        await using var app = BffApp.StartAnotherInstance();
        var provider = app.IdentityProvider;
        var subject = NewSubject();
        var (cookie, key) = await SignInAsync(app, $"test_sub={subject}&test_expires_in=60&test_refresh_expires_in=3600");
        var gate = provider.HoldRefreshes(subject);
        using var browser = Cookieless(app);

        // Act
        var firstCall = browser.SendAsync(ApiRequest(cookie));
        await gate.Arrived.Task.WaitAsync(Timeout);
        var secondCall = browser.SendAsync(ApiRequest(cookie));
        await WaitForInProcessWaiterAsync(app);
        gate.Released.SetResult();
        var responses = await Task.WhenAll(firstCall, secondCall).WaitAsync(Timeout);

        // Assert
        provider.RefreshCount(subject).ShouldBe(1);
        var newAccessToken = provider.IssuedAccessTokens(subject)[1];
        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.ReadEchoAsync()).Headers["Authorization"].ShouldBe($"Bearer {newAccessToken}");
        }

        (await StoredTokensAsync(app, key)).AccessToken.ShouldBe(newAccessToken);
    }

    [Test]
    public async Task an_abort_of_the_call_that_runs_the_refresh_does_not_stop_the_refresh_of_its_waiter()
    {
        // Arrange
        await using var app = BffApp.StartAnotherInstance();
        var provider = app.IdentityProvider;
        var subject = NewSubject();
        var (cookie, key) = await SignInAsync(app, $"test_sub={subject}&test_expires_in=60&test_refresh_expires_in=3600");
        var gate = provider.HoldRefreshes(subject);
        using var browser = Cookieless(app);
        using var abort = new CancellationTokenSource();

        // Act
        var firstCall = browser.SendAsync(ApiRequest(cookie), abort.Token);
        await gate.Arrived.Task.WaitAsync(Timeout);
        var secondCall = browser.SendAsync(ApiRequest(cookie));
        await WaitForInProcessWaiterAsync(app);
        // The test server cancels RequestAborted at once, and ends the client call when the request ends.
        await abort.CancelAsync();
        gate.Released.SetResult();
        var response = await secondCall.WaitAsync(Timeout);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(firstCall.WaitAsync(Timeout));

        provider.RefreshCount(subject).ShouldBe(1);
        var newAccessToken = provider.IssuedAccessTokens(subject)[1];
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadEchoAsync()).Headers["Authorization"].ShouldBe($"Bearer {newAccessToken}");
        var tokens = await StoredTokensAsync(app, key);
        tokens.AccessToken.ShouldBe(newAccessToken);
        tokens.RefreshToken.ShouldBe(provider.IssuedRefreshTokens(subject)[1]);
    }

    [Test]
    public async Task an_abort_after_the_identity_provider_rotated_the_refresh_token_still_writes_the_new_tokens()
    {
        // Arrange
        var write = new PauseAtCommand(SessionWrite);
        await using var app = BffApp.StartAnotherInstance(services: services =>
            services.ConfigureDbContext<BffDbContext>(options => options.AddInterceptors(write)));
        var provider = app.IdentityProvider;
        var subject = NewSubject();
        var (cookie, key) = await SignInAsync(app, $"test_sub={subject}&test_expires_in=60&test_refresh_expires_in=3600");
        write.Arm();
        using var browser = Cookieless(app);
        using var abort = new CancellationTokenSource();

        // Act
        var call = browser.SendAsync(ApiRequest(cookie), abort.Token);
        await write.Arrived.WaitAsync(Timeout);
        await abort.CancelAsync();
        write.Release();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(call.WaitAsync(Timeout));

        provider.RefreshCount(subject).ShouldBe(1);
        using var timeout = new CancellationTokenSource(Timeout);
        var newRefreshToken = provider.IssuedRefreshTokens(subject)[1];
        while ((await StoredTokensAsync(app, key)).RefreshToken != newRefreshToken)
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        (await StoredTokensAsync(app, key)).AccessToken.ShouldBe(provider.IssuedAccessTokens(subject)[1]);
    }

    [Test]
    public async Task a_hung_identity_provider_reaches_the_token_request_timeout_and_keeps_the_session()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = StartWithClock(clock);
        var provider = app.IdentityProvider;
        var subject = NewSubject();
        var (cookie, key) = await SignInAsync(app, $"test_sub={subject}&test_expires_in=60");
        var gate = provider.HoldRefreshes(subject);
        using var browser = Cookieless(app);
        try
        {
            // Act
            var call = browser.SendAsync(ApiRequest(cookie));
            await gate.Arrived.Task.WaitAsync(Timeout);
            clock.Advance(ClockedTokenRequestTimeout);
            var response = await call.WaitAsync(Timeout);

            // Assert
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.ReadEchoAsync()).Headers["Authorization"].ShouldBe($"Bearer {provider.IssuedAccessTokens(subject).Single()}");
            provider.RefreshCount(subject).ShouldBe(1);
            (await app.WithStoreAsync(store => store.GetAsync(key, CancellationToken.None))).ShouldNotBeNull();
        }
        finally
        {
            gate.Released.TrySetResult();
        }
    }

    [Test]
    public async Task a_wait_for_the_refresh_of_another_call_outlasts_the_token_request()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = StartWithClock(clock);
        var provider = app.IdentityProvider;
        var subject = NewSubject();
        var (cookie, key) = await SignInAsync(app, $"test_sub={subject}&test_expires_in=60");
        var gate = provider.HoldRefreshes(subject);
        using var browser = Cookieless(app);
        try
        {
            // Act
            var firstCall = browser.SendAsync(ApiRequest(cookie));
            await gate.Arrived.Task.WaitAsync(Timeout);
            var secondCall = browser.SendAsync(ApiRequest(cookie));
            await WaitForInProcessWaiterAsync(app);
            clock.Advance(ClockedTokenRequestTimeout - TimeSpan.FromMilliseconds(1));
            gate.Released.SetResult();
            var responses = await Task.WhenAll(firstCall, secondCall).WaitAsync(Timeout);

            // Assert
            provider.RefreshCount(subject).ShouldBe(1);
            var newAccessToken = provider.IssuedAccessTokens(subject)[1];
            foreach (var response in responses)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                (await response.ReadEchoAsync()).Headers["Authorization"].ShouldBe($"Bearer {newAccessToken}");
            }

            (await StoredTokensAsync(app, key)).AccessToken.ShouldBe(newAccessToken);
        }
        finally
        {
            gate.Released.TrySetResult();
        }
    }

    [Test]
    public async Task a_wait_for_the_refresh_of_another_call_reaches_its_limit()
    {
        // Arrange
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var write = new PauseAtCommand(SessionWrite);
        await using var app = StartWithClock(clock, pause: write);
        var provider = app.IdentityProvider;
        var subject = NewSubject();
        var (cookie, key) = await SignInAsync(app, $"test_sub={subject}&test_expires_in=110");
        write.Arm();
        using var browser = Cookieless(app);

        // Act
        var firstCall = browser.SendAsync(ApiRequest(cookie));
        await write.Arrived.WaitAsync(Timeout);
        var secondCall = browser.SendAsync(ApiRequest(cookie));
        await WaitForInProcessWaiterAsync(app);
        clock.Advance(app.Services.GetRequiredService<IOptionsMonitor<RefreshOptions>>().CurrentValue.InProcessWaitLimit);

        // Assert
        var second = await secondCall.WaitAsync(Timeout);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var currentAccessToken = provider.IssuedAccessTokens(subject)[0];
        (await second.ReadEchoAsync()).Headers["Authorization"].ShouldBe($"Bearer {currentAccessToken}");
        firstCall.IsCompleted.ShouldBeFalse();

        write.Release();
        var first = await firstCall.WaitAsync(Timeout);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.ReadEchoAsync()).Headers["Authorization"].ShouldBe($"Bearer {provider.IssuedAccessTokens(subject)[1]}");
        provider.RefreshCount(subject).ShouldBe(1);
        (await app.WithStoreAsync(store => store.GetAsync(key, CancellationToken.None))).ShouldNotBeNull();
    }

    internal static BffApp StartWithClock(FakeTimeProvider clock, FakeIdentityProvider? identityProvider = null,
        IReadOnlyDictionary<string, string?>? settings = null, PauseAtCommand? pause = null) => BffApp.StartAnotherInstance(
        new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>())
        {
            ["Bff:Refresh:TokenRequestTimeout"] = ClockedTokenRequestTimeout.ToString(),
        },
        services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            if (pause is not null)
                services.ConfigureDbContext<BffDbContext>(options => options.AddInterceptors(pause));
        },
        identityProvider);

    /// <summary>
    /// Signs in on <paramref name="app"/> and returns the session cookie and the BFF session key.
    /// </summary>
    internal static async Task<(string Cookie, string Key)> SignInAsync(BffApp app, string providerQuery)
    {
        using var browser = app.CreateBrowser();
        var callback = await BffApp.SignInAsync(browser, providerQuery: providerQuery);
        return (BffApp.SessionCookieValue(callback)!, app.SessionKeyIn(callback)!);
    }

    /// <summary>
    /// A client with no cookie container. Each request carries the cookie that the test gives it.
    /// </summary>
    internal static HttpClient Cookieless(BffApp app) =>
        app.CreateClient(new() { BaseAddress = BffApp.Origin, HandleCookies = false, AllowAutoRedirect = false });

    internal static HttpRequestMessage ApiRequest(string cookie, string path = "/api/v1/items")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-CSRF", "1");
        request.Headers.Add("Cookie", $"__Host-bff={cookie}");
        return request;
    }

    internal static async Task<SessionTokens> StoredTokensAsync(BffApp app, string key)
    {
        var row = (await app.WithStoreAsync(store => store.GetAsync(key, CancellationToken.None))).ShouldNotBeNull();
        return app.Services.GetRequiredService<BffTicketStore>().TokensOf(row).ShouldNotBeNull();
    }

    /// <summary>
    /// Waits until a request on <paramref name="app"/> waits for the refresh of another request in process.
    /// </summary>
    internal static async Task WaitForInProcessWaiterAsync(BffApp app)
    {
        var refreshLock = (RefreshLock)app.Services.GetRequiredService<IUserTokenRequestConcurrencyControl>();
        using var timeout = new CancellationTokenSource(Timeout);
        while (refreshLock.WaitingRequests == 0)
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
    }

    private static string NewSubject() => $"user-lock-{Guid.NewGuid():N}";
}
