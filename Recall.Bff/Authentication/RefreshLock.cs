using System.Collections.Concurrent;
using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;
using Recall.Bff.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Recall.Bff.Authentication;

/// <summary>
/// The caller must not give <c>RequestAborted</c> to token management, because token management gives its token to the shared
/// refresh too.
/// </summary>
public sealed class RefreshLock(
    IHttpContextAccessor httpContextAccessor,
    BffTicketStore ticketStore,
    IStoreTokensInAuthenticationProperties tokensInProperties,
    IOptionsMonitor<RefreshOptions> options,
    TimeProvider timeProvider) : IUserTokenRequestConcurrencyControl
{
    public const string LockTimeoutError = "bff_refresh_lock_timeout";

    public const string SessionEndedError = "bff_session_ended";

    private static readonly ILogger Logger = Log.ForContext<RefreshLock>();

    private readonly ConcurrentDictionary<string, TaskCompletionSource<TokenResult<UserToken>>> _inFlight = new(StringComparer.Ordinal);

    private int _waiting;

    /// <summary>
    /// The number of requests in this instance that wait now for the refresh of another request.
    /// </summary>
    public int WaitingRequests => Volatile.Read(ref _waiting);

    /// <summary>
    /// Runs <paramref name="tokenRetriever"/> one time for all the requests with the same refresh token. It does not
    /// use <paramref name="ct"/>: a waiter stops at the <c>RequestAborted</c> of its own request.
    /// </summary>
    public async Task<TokenResult<UserToken>> ExecuteWithConcurrencyControlAsync(UserRefreshToken key,
        Func<Task<TokenResult<UserToken>>> tokenRetriever, CancellationToken ct = default)
    {
        var refreshToken = key.RefreshToken.ToString();
        var own = new TaskCompletionSource<TokenResult<UserToken>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = _inFlight.GetOrAdd(refreshToken, own);
        if (!ReferenceEquals(refresh, own))
            return await WaitForRefreshAsync(refresh.Task);

        // This request runs the refresh, in its own scope and HttpContext.
        try
        {
            var result = await RefreshOnceAsync(tokenRetriever);
            own.SetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            own.SetException(ex);
            // Observed here, so that a refresh with no waiters does not raise UnobservedTaskException.
            _ = own.Task.Exception;
            throw;
        }
        finally
        {
            // Only this refresh. A later refresh with the same token can have its own entry by now.
            _inFlight.TryRemove(new KeyValuePair<string, TaskCompletionSource<TokenResult<UserToken>>>(refreshToken, own));
        }
    }

    /// <summary>
    /// True when <paramref name="error"/> ends the BFF session: the identity provider rejected the refresh token
    /// (<c>invalid_grant</c>), or the BFF session ended during the wait. Each other failure is temporary.
    /// </summary>
    public static bool EndsSession(string? error) =>
        RefreshFailureMetrics.KindOf(error) == RefreshFailureKind.Permanent
        || string.Equals(error, SessionEndedError, StringComparison.Ordinal);

    /// <summary>
    /// Waits for the refresh of another request in this instance, at most <see cref="RefreshOptions.InProcessWaitLimit"/>,
    /// the longest run of that refresh. The wait stops when the client of this request goes away.
    /// </summary>
    private async Task<TokenResult<UserToken>> WaitForRefreshAsync(Task<TokenResult<UserToken>> refresh)
    {
        var httpContext = CurrentHttpContext();
        var waitLimit = options.CurrentValue.InProcessWaitLimit;
        // Start the timer before the count goes up. Then each request in the count has its timer.
        var wait = refresh.WaitAsync(waitLimit, timeProvider, httpContext.RequestAborted);
        Interlocked.Increment(ref _waiting);
        try
        {
            return await wait;
        }
        catch (TimeoutException)
        {
            Telemetry.RecordRefreshFailure(RefreshFailureKind.Temporary);
            Logger.Here().Warning("Kept the BFF session {SessionKey} with no refresh, because the refresh of another request did not end within {WaitLimit}",
                Fingerprint.Of(httpContext.RequestServices.GetRequiredService<BffSessionRequest>().Key ?? ""), waitLimit);
            return TokenResult.Failure(LockTimeoutError);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    private HttpContext CurrentHttpContext() => httpContextAccessor.HttpContext
        ?? throw new InvalidOperationException("The refresh lock needs the HttpContext of the request.");

    private async Task<TokenResult<UserToken>> RefreshOnceAsync(Func<Task<TokenResult<UserToken>>> tokenRetriever)
    {
        var services = CurrentHttpContext().RequestServices;
        var request = services.GetRequiredService<BffSessionRequest>();
        if (request.Key is not { } sessionKey)
            return TokenResult.Failure(SessionEndedError);

        var store = services.GetRequiredService<IBffSessionStore>();
        var fingerprint = Fingerprint.Of(sessionKey);

        var row = await store.GetAsync(sessionKey, CancellationToken.None);
        if (row is null || ticketStore.TicketOf(row) is not { } ticket)
        {
            Logger.Here().Information("Skipped the refresh of the BFF session {SessionKey}, because the BFF session ended",
                fingerprint);
            return TokenResult.Failure(SessionEndedError);
        }

        var storedTokens = SessionTokens.From(ticket.Properties);
        if (storedTokens != request.Tokens)
        {
            Logger.Here().Debug("Used the tokens that another request refreshed for the BFF session {SessionKey}", fingerprint);
            return StoredToken(ticket.Properties);
        }

        request.Remember(row, storedTokens);
        var result = await tokenRetriever();

        var ended = !result.WasSuccessful(out _, out var failure)
                    && RefreshFailureMetrics.KindOf(failure.Error) == RefreshFailureKind.Permanent
                    && await store.DeleteAsync(sessionKey, CancellationToken.None) is not null;

        ReportRefresh(result, ended, fingerprint);
        return result;
    }

    private static void ReportRefresh(TokenResult<UserToken> result, bool ended, string fingerprint)
    {
        if (result.WasSuccessful(out _, out var failure))
        {
            Logger.Here().Debug("Refreshed the tokens of the BFF session {SessionKey}", fingerprint);
            return;
        }

        if (RefreshFailureMetrics.KindOf(failure.Error) == RefreshFailureKind.Temporary)
        {
            Logger.Here().Warning("Kept the BFF session {SessionKey} after a temporary refresh failure: {Error} {ErrorDescription}",
                fingerprint, failure.Error, failure.ErrorDescription);
            return;
        }

        if (ended)
            Telemetry.RecordSessionsEnded(SessionEndReason.RefreshFailure);
        if (string.Equals(failure.Error, RefreshIdTokenValidation.InvalidIdTokenError, StringComparison.Ordinal))
        {
            // RefreshIdTokenValidation logs the reason
            Logger.Here().Warning("Ended the BFF session {SessionKey}, because the ID token of its refresh response failed the checks",
                fingerprint);
            return;
        }

        // An expected end: the refresh token expired or was revoked, or the IdP session ended. Not a fault of the BFF.
        Logger.Here().Information("Ended the BFF session {SessionKey}, because the identity provider rejected its refresh token with {Error}",
            fingerprint, failure.Error);
    }

    private TokenResult<UserToken> StoredToken(AuthenticationProperties properties) =>
        tokensInProperties.GetUserToken(properties).WasSuccessful(out var stored, out var failure)
            ? stored.TokenForSpecifiedParameters is { } token ? token : TokenResult.Failure("The BFF session has no access token")
            : failure;
}
