namespace Recall.Server.Auth;

using System.Diagnostics.CodeAnalysis;
using Databases;
using Domain;
using Entities;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

/// <summary>What a valid credential proves.</summary>
public sealed record SessionPrincipal(string SessionId, string UserId, SessionKind Kind, DateTimeOffset AccessExpiresAt);

/// <summary>A new web session. The cookie value exists as text only here and in the response.</summary>
public sealed record WebSession(string SessionId, string CookieValue, DateTimeOffset ExpiresAt)
{
    public const string CookiePrefix = "rcl_ck_";

    public override string ToString() => $"[web session {SessionId}]";
}

/// <summary>The answer to a refresh. No pair means <c>invalid_grant</c>.</summary>
public sealed record RefreshResult(TokenPair? Pair, RefreshDecision? Decision);

/// <summary>
/// The one owner of the session, attempt, and code rows. It sits on the context with no MediatR, because the hot path
/// cannot pay for a pipeline. All of its tables have no tenant, so it never reads the caller and never pins the tenant
/// scope of the request.
/// </summary>
[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs",
    Justification = "auth_sessions, auth_login_attempts, and auth_app_codes are [NotAudited], so these writes need no audit entry.")]
public sealed class SessionStore(AppDbContext dbContext, TimeProvider timeProvider)
{
    private const int SweepBatchSize = 500;

    private readonly ILogger _logger = Log.ForContext<SessionStore>();

    /// <summary>One read of the unique index and no write. It runs on each authenticated request.</summary>
    public async Task<SessionPrincipal?> FindByAccessAsync(TokenHash hash, SessionKind kind, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var row = await dbContext.Set<AuthSession>()
            .AsNoTracking()
            .TagWithOperationCallSite("auth.sessions.find_by_access")
            .Where(s => s.AccessHash == hash.Hex && s.ClientKind == kind && s.RevokedAt == null && s.AccessExpiresAt > now)
            .Select(s => new { s.Id, s.UserId, s.AccessExpiresAt })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new SessionPrincipal(row.Id, row.UserId, kind, row.AccessExpiresAt);
    }

    public async Task<WebSession> StartWebAsync(string userId, string loginMode, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cookieValue = WebSession.CookiePrefix + Secrets.New();
        var expiresAt = now + SessionPolicy.WebIdle;
        var session = new AuthSession
        {
            Id = EntityId.New(AuthSession.IdPrefix),
            UserId = userId,
            ClientKind = SessionKind.Web,
            LoginMode = loginMode,
            AccessHash = TokenHash.Of(cookieValue).Hex,
            AccessExpiresAt = expiresAt,
            ExpiresAt = expiresAt,
            CreatedAt = now,
            LastSlidAt = now,
        };
        dbContext.Set<AuthSession>().Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.Here().Information("Started web session {SessionId} for user {UserId}", session.Id, userId);
        return new WebSession(session.Id, cookieValue, expiresAt);
    }

    public async Task<TokenPair> StartNativeAsync(string userId, string loginMode, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var refresh = RefreshToken.New();
        var pair = TokenDerivation.Random(refresh.Family, now + SessionPolicy.BearerLifetime);
        var session = new AuthSession
        {
            Id = EntityId.New(AuthSession.IdPrefix),
            UserId = userId,
            ClientKind = SessionKind.Native,
            LoginMode = loginMode,
            AccessHash = TokenHash.Of(pair.AccessToken).Hex,
            AccessExpiresAt = pair.AccessExpiresAt,
            RefreshFamilyHash = TokenHash.Of(refresh.Family).Hex,
            RefreshHash = TokenHash.Of(pair.RefreshToken).Hex,
            ExpiresAt = now + SessionPolicy.RefreshIdle,
            CreatedAt = now,
        };
        dbContext.Set<AuthSession>().Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.Here().Information("Started native session {SessionId} for user {UserId}", session.Id, userId);
        return pair;
    }

    /// <summary>
    /// One read by family and one conditional update. Two copies of one request can race, so a lost update reads again
    /// and decides once more. A second lost update refuses with no revoke, because it proves no replay.
    /// </summary>
    public async Task<RefreshResult> RefreshAsync(RefreshToken token, RetryKey? retryKey, CancellationToken cancellationToken)
    {
        var familyHash = TokenHash.Of(token.Family).Hex;
        for (var run = 0; run < 2; run++)
        {
            var row = await dbContext.Set<AuthSession>()
                .AsNoTracking()
                .TagWithOperationCallSite("auth.sessions.refresh_read")
                .Where(s => s.RefreshFamilyHash == familyHash)
                .Select(s => new { s.Id, s.RefreshHash, s.ExpiresAt, s.RevokedAt, s.AccessExpiresAt })
                .FirstOrDefaultAsync(cancellationToken);
            if (row is null)
                return new RefreshResult(null, null);

            var now = timeProvider.GetUtcNow();
            var decision = RefreshRule.Classify(new RefreshState(row.RefreshHash!, row.ExpiresAt, row.RevokedAt), token, retryKey, now);
            switch (decision)
            {
                case RefreshDecision.Revoked or RefreshDecision.Expired:
                    return new RefreshResult(null, decision);

                case RefreshDecision.Replay:
                    await RevokeAsync(row.Id, RevokeReason.RefreshReplay, cancellationToken);
                    _logger.Here().Warning("Ended session {SessionId} because an old refresh token came back", row.Id);
                    return new RefreshResult(null, decision);

                case RefreshDecision.RotateRetry:
                    return new RefreshResult(TokenDerivation.Derive(token, retryKey!.Value, row.AccessExpiresAt), decision);

                case RefreshDecision.Rotate:
                    var accessExpiresAt = now + SessionPolicy.BearerLifetime;
                    var pair = retryKey is { } key
                        ? TokenDerivation.Derive(token, key, accessExpiresAt)
                        : TokenDerivation.Random(token.Family, accessExpiresAt);
                    var accessHash = TokenHash.Of(pair.AccessToken).Hex;
                    var refreshHash = TokenHash.Of(pair.RefreshToken).Hex;
                    var expiresAt = now + SessionPolicy.RefreshIdle;
                    var updated = await dbContext.Set<AuthSession>()
                        .TagWithOperationCallSite("auth.sessions.refresh_rotate")
                        .Where(s => s.Id == row.Id && s.RefreshHash == row.RefreshHash && s.RevokedAt == null)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(s => s.AccessHash, accessHash)
                            .SetProperty(s => s.AccessExpiresAt, accessExpiresAt)
                            .SetProperty(s => s.RefreshHash, refreshHash)
                            .SetProperty(s => s.ExpiresAt, expiresAt), cancellationToken);
                    if (updated == 1)
                        return new RefreshResult(pair, decision);
                    break;
            }
        }

        return new RefreshResult(null, null);
    }

    /// <summary>
    /// Writes only if the last slide is older than <see cref="SessionPolicy.WebSlideEvery"/>, and never past the absolute
    /// limit. It returns the new expiry when the caller must send the cookie again.
    /// </summary>
    public async Task<DateTimeOffset?> SlideWebAsync(string sessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var row = await dbContext.Set<AuthSession>()
            .AsNoTracking()
            .TagWithOperationCallSite("auth.sessions.slide_read")
            .Where(s => s.Id == sessionId && s.ClientKind == SessionKind.Web && s.RevokedAt == null)
            .Select(s => new { s.CreatedAt, s.ExpiresAt, s.LastSlidAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null || now - (row.LastSlidAt ?? row.CreatedAt) < SessionPolicy.WebSlideEvery)
            return null;

        var limit = row.CreatedAt + SessionPolicy.WebAbsolute;
        var wanted = now + SessionPolicy.WebIdle;
        var expiresAt = wanted < limit ? wanted : limit;
        if (expiresAt <= row.ExpiresAt)
            return null;

        var updated = await dbContext.Set<AuthSession>()
            .TagWithOperationCallSite("auth.sessions.slide_write")
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.ExpiresAt, expiresAt)
                .SetProperty(s => s.AccessExpiresAt, expiresAt)
                .SetProperty(s => s.LastSlidAt, now), cancellationToken);
        return updated == 1 ? expiresAt : null;
    }

    public async Task RevokeAsync(string sessionId, RevokeReason reason, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await dbContext.Set<AuthSession>()
            .TagWithOperationCallSite("auth.sessions.revoke")
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.RevokedAt, now)
                .SetProperty(s => s.RevokedReason, reason), cancellationToken);
    }

    /// <summary>A token of the family is enough, so that a client can end a session with an old token.</summary>
    public async Task RevokeByRefreshAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        var familyHash = TokenHash.Of(token.Family).Hex;
        var now = timeProvider.GetUtcNow();
        await dbContext.Set<AuthSession>()
            .TagWithOperationCallSite("auth.sessions.revoke_by_refresh")
            .Where(s => s.RefreshFamilyHash == familyHash && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.RevokedAt, now)
                .SetProperty(s => s.RevokedReason, RevokeReason.Logout), cancellationToken);
    }

    public async Task RevokeOthersAsync(string userId, string keepSessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await dbContext.Set<AuthSession>()
            .TagWithOperationCallSite("auth.sessions.revoke_others")
            .Where(s => s.UserId == userId && s.Id != keepSessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.RevokedAt, now)
                .SetProperty(s => s.RevokedReason, RevokeReason.RevokeOthers), cancellationToken);
    }

    public async Task AddAttemptAsync(LoginAttempt attempt, CancellationToken cancellationToken)
    {
        dbContext.Set<LoginAttempt>().Add(attempt);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A live attempt only, and no write, so that a stranger with a guessed state burns nothing.</summary>
    public async Task<LoginAttempt?> FindAttemptAsync(TokenHash state, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await dbContext.Set<LoginAttempt>()
            .AsNoTracking()
            .TagWithOperationCallSite("auth.attempts.find")
            .FirstOrDefaultAsync(a => a.StateHash == state.Hex && a.ExpiresAt > now, cancellationToken);
    }

    /// <summary>The caller that deletes the row wins. Each other caller gets false.</summary>
    public async Task<bool> ConsumeAttemptAsync(LoginAttempt attempt, CancellationToken cancellationToken)
    {
        var deleted = await dbContext.Set<LoginAttempt>()
            .TagWithOperationCallSite("auth.attempts.consume")
            .Where(a => a.StateHash == attempt.StateHash)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 1;
    }

    /// <summary>Returns the code that the app trades for its tokens. The value exists as text only here and in the response.</summary>
    public async Task<string> AddAppCodeAsync(
        string userId, string loginMode, string redirectUri, string challenge, string? clientState, CancellationToken cancellationToken)
    {
        var code = Secrets.New();
        dbContext.Set<AppCode>().Add(new AppCode
        {
            CodeHash = TokenHash.Of(code).Hex,
            UserId = userId,
            LoginMode = loginMode,
            RedirectUri = redirectUri,
            Challenge = challenge,
            ClientState = clientState,
            ExpiresAt = timeProvider.GetUtcNow() + SessionPolicy.AppCodeLifetime,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return code;
    }

    /// <summary>A live code only, and no write. The confirm page reads the code and leaves it for the app.</summary>
    public async Task<AppCode?> FindAppCodeAsync(TokenHash code, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await dbContext.Set<AppCode>()
            .AsNoTracking()
            .TagWithOperationCallSite("auth.app_codes.find")
            .FirstOrDefaultAsync(c => c.CodeHash == code.Hex && c.ExpiresAt > now, cancellationToken);
    }

    /// <summary>The caller that deletes the row gets it. Each other caller gets null.</summary>
    public async Task<AppCode?> ConsumeAppCodeAsync(TokenHash code, CancellationToken cancellationToken)
    {
        var found = await FindAppCodeAsync(code, cancellationToken);
        if (found is null)
            return null;

        var deleted = await dbContext.Set<AppCode>()
            .TagWithOperationCallSite("auth.app_codes.consume")
            .Where(c => c.CodeHash == code.Hex)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 1 ? found : null;
    }

    /// <summary>Deletes expired attempts and codes, and sessions that ended long enough ago. Each statement deletes one bounded batch.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var endedBefore = now - SessionPolicy.KeepEndedSession;
        await DeleteBatchesAsync(
            () => dbContext.Set<LoginAttempt>()
                .TagWithOperationCallSite("auth.sweep.attempts")
                .Where(a => a.ExpiresAt <= now)
                .OrderBy(a => a.ExpiresAt)
                .Take(SweepBatchSize)
                .ExecuteDeleteAsync(cancellationToken));
        await DeleteBatchesAsync(
            () => dbContext.Set<AppCode>()
                .TagWithOperationCallSite("auth.sweep.app_codes")
                .Where(c => c.ExpiresAt <= now)
                .OrderBy(c => c.ExpiresAt)
                .Take(SweepBatchSize)
                .ExecuteDeleteAsync(cancellationToken));
        await DeleteBatchesAsync(
            () => dbContext.Set<AuthSession>()
                .TagWithOperationCallSite("auth.sweep.sessions")
                .Where(s => s.ExpiresAt <= endedBefore || s.RevokedAt <= endedBefore)
                .OrderBy(s => s.ExpiresAt)
                .Take(SweepBatchSize)
                .ExecuteDeleteAsync(cancellationToken));
    }

    private static async Task DeleteBatchesAsync(Func<Task<int>> deleteBatch)
    {
        while (await deleteBatch() == SweepBatchSize)
        {
        }
    }
}
