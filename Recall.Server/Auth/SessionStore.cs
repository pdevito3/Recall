namespace Recall.Server.Auth;

using System.Diagnostics.CodeAnalysis;
using Databases;
using Domain;
using Domain.AuditEntries;
using Domain.PersonalAccessTokens;
using Domain.Users;
using Entities;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;

public sealed record SessionPrincipal(string SessionId, string UserId, SessionKind Kind, DateTimeOffset AccessExpiresAt);

public sealed record WebSession(string SessionId, string CookieValue, DateTimeOffset ExpiresAt)
{
    public const string CookiePrefix = "rcl_ck_";

    public override string ToString() => $"[web session {SessionId}]";
}

public sealed record RefreshResult(TokenPair? Pair, RefreshDecision? Decision);

/// <summary>A live personal access token that a request presents. <paramref name="UseMarkDue"/> is true when its last use is over an hour old.</summary>
public sealed record PersonalAccessTokenPrincipal(string TokenId, string UserId, TokenGrant Grant, DateTimeOffset AccessExpiresAt, bool UseMarkDue);

/// <summary>A personal access token as a list shows it. It holds no value and no hash.</summary>
public sealed record PersonalAccessTokenRow(
    string Id,
    string UserId,
    string Name,
    TokenGrant Grant,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

/// <summary>The one answer that holds the value of a token. The Server keeps only its hash.</summary>
public sealed record NewPersonalAccessToken(PersonalAccessTokenRow Token, string Value)
{
    public const string Prefix = "rcl_pat_";

    public override string ToString() => $"[personal access token {Token.Id}]";
}

[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs",
    Justification = "auth_sessions, auth_login_attempts, and auth_app_codes are [NotAudited], so these writes need no audit entry.")]
public sealed class SessionStore(AppDbContext dbContext, TimeProvider timeProvider)
{
    private const int SweepBatchSize = 500;

    private readonly ILogger _logger = Log.ForContext<SessionStore>();

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

    public Task<string?> LoginModeOfWebSessionEvenIfEndedAsync(TokenHash hash, CancellationToken cancellationToken)
        => dbContext.Set<AuthSession>()
            .AsNoTracking()
            .TagWithOperationCallSite("auth.sessions.login_mode_of_web")
            .Where(s => s.AccessHash == hash.Hex && s.ClientKind == SessionKind.Web)
            .Select(s => s.LoginMode)
            .FirstOrDefaultAsync(cancellationToken);

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

    public async Task<PersonalAccessTokenPrincipal?> FindPersonalAccessTokenAsync(TokenHash hash, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var row = await dbContext.Set<AuthSession>()
            .AsNoTracking()
            .TagWithOperationCallSite("auth.sessions.find_personal_access_token")
            .Where(s => s.AccessHash == hash.Hex && s.ClientKind == SessionKind.Pat && s.RevokedAt == null && s.AccessExpiresAt > now)
            .Select(s => new { s.Id, s.UserId, s.Grant, s.AccessExpiresAt, s.LastUsedAt })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null
            ? null
            : new PersonalAccessTokenPrincipal(row.Id, row.UserId, TokenGrant.Decode(row.Grant!), row.AccessExpiresAt,
                row.LastUsedAt is not { } lastUsed || now - lastUsed > SessionPolicy.PersonalAccessTokenUseMarkEvery);
    }

    /// <summary>
    /// Adds the token row and the audit action of its create in one save. <paramref name="expiresAt"/> is null for a
    /// token that never expires.
    /// </summary>
    public async Task<NewPersonalAccessToken> StartPersonalAccessTokenAsync(
        User user, PersonalAccessTokenName name, TokenGrant grant, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var value = NewPersonalAccessToken.Prefix + Secrets.New();
        var storedExpiry = expiresAt ?? SessionPolicy.PersonalAccessTokenNever;
        var session = new AuthSession
        {
            Id = EntityId.New(PatClaims.IdPrefix),
            UserId = user.Id,
            ClientKind = SessionKind.Pat,
            LoginMode = SessionKindText.Pat,
            AccessHash = TokenHash.Of(value).Hex,
            AccessExpiresAt = storedExpiry,
            ExpiresAt = storedExpiry,
            CreatedAt = now,
            Name = name.Value,
            Grant = grant.Encode(),
        };
        dbContext.Set<AuthSession>().Add(session);
        dbContext.RecordAction(new AccessTokenCreated(AuditSubject.Of(user), session.Id, name.Value, session.Grant, expiresAt));
        await dbContext.SaveChangesAsync(cancellationToken);

        _logger.Here().Information("Started personal access token {TokenId} for user {UserId}", session.Id, user.Id);
        return new NewPersonalAccessToken(RowOf(session), value);
    }

    public async Task<IReadOnlyList<PersonalAccessTokenRow>> ListPersonalAccessTokensAsync(User user, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<AuthSession>()
            .AsNoTracking()
            .TagWithOperationCallSite("auth.sessions.list_personal_access_tokens")
            .Where(s => s.UserId == user.Id && s.ClientKind == SessionKind.Pat)
            .OrderByDescending(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(RowOf).ToList();
    }

    /// <summary>The user ID of the personal access token, or null when no such token exists.</summary>
    public Task<string?> OwnerOfPersonalAccessTokenAsync(string tokenId, CancellationToken cancellationToken)
        => dbContext.Set<AuthSession>()
            .AsNoTracking()
            .TagWithOperationCallSite("auth.sessions.owner_of_personal_access_token")
            .Where(s => s.Id == tokenId && s.ClientKind == SessionKind.Pat)
            .Select(s => s.UserId)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Revokes a live token of <paramref name="user"/> and records the audit action in one save. A token that has ended
    /// stays as it is, so a second revoke changes nothing. False when the user has no such token.
    /// </summary>
    /// <exception cref="Exceptions.StaleVersionException">Another write ended the token after the read, and the save changed nothing.</exception>
    // A revoke takes no cancellation token, as RevokeAsync does.
    public async Task<bool> RevokePersonalAccessTokenAsync(User user, string tokenId)
    {
        var session = await dbContext.Set<AuthSession>()
            .TagWithOperationCallSite("auth.sessions.revoke_personal_access_token_read")
            .FirstOrDefaultAsync(s => s.Id == tokenId && s.UserId == user.Id && s.ClientKind == SessionKind.Pat, CancellationToken.None);
        if (session is null)
            return false;

        var now = timeProvider.GetUtcNow();
        if (session.RevokedAt is not null || session.AccessExpiresAt <= now)
            return true;

        var entry = dbContext.Entry(session);
        entry.Property(s => s.RevokedAt).CurrentValue = now;
        entry.Property(s => s.RevokedReason).CurrentValue = RevokeReason.TokenRevoked;
        var row = RowOf(session);
        dbContext.RecordAction(new AccessTokenRevoked(AuditSubject.Of(user), session.Id, row.Name, session.Grant!, row.ExpiresAt));
        await dbContext.SaveChangesAsync(CancellationToken.None);

        _logger.Here().Information("Revoked personal access token {TokenId} of user {UserId}", session.Id, user.Id);
        return true;
    }

    /// <summary>
    /// Marks the last use of a token. The condition makes a second mark within the hour a no-op, so two requests that
    /// both find the mark due write it once.
    /// </summary>
    public async Task<bool> NotePersonalAccessTokenUseAsync(string tokenId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var dueBefore = now - SessionPolicy.PersonalAccessTokenUseMarkEvery;
        var updated = await dbContext.Set<AuthSession>()
            .TagWithOperationCallSite("auth.sessions.note_personal_access_token_use")
            .Where(s => s.Id == tokenId && s.ClientKind == SessionKind.Pat && (s.LastUsedAt == null || s.LastUsedAt < dueBefore))
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastUsedAt, now), cancellationToken);
        return updated == 1;
    }

    private static PersonalAccessTokenRow RowOf(AuthSession session)
        => new(
            session.Id,
            session.UserId,
            session.Name!,
            TokenGrant.Decode(session.Grant!),
            session.CreatedAt,
            session.ExpiresAt == SessionPolicy.PersonalAccessTokenNever ? null : session.ExpiresAt,
            session.LastUsedAt,
            session.RevokedAt);

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
                    await RevokeAsync(row.Id, RevokeReason.RefreshReplay);
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

    // A revoke takes no cancellation token. The caller that asked for it can go away while the write waits in the line,
    // and the session must still end. The limit of the writer line bounds the wait.
    public async Task RevokeAsync(string sessionId, RevokeReason reason)
    {
        var now = timeProvider.GetUtcNow();
        await dbContext.Set<AuthSession>()
            .TagWithOperationCallSite("auth.sessions.revoke")
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.RevokedAt, now)
                .SetProperty(s => s.RevokedReason, reason), CancellationToken.None);
    }

    public async Task RevokeByRefreshAsync(RefreshToken token)
    {
        var familyHash = TokenHash.Of(token.Family).Hex;
        var now = timeProvider.GetUtcNow();
        await dbContext.Set<AuthSession>()
            .TagWithOperationCallSite("auth.sessions.revoke_by_refresh")
            .Where(s => s.RefreshFamilyHash == familyHash && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.RevokedAt, now)
                .SetProperty(s => s.RevokedReason, RevokeReason.Logout), CancellationToken.None);
    }

    public async Task RevokeOthersAsync(string userId, string keepSessionId)
    {
        var now = timeProvider.GetUtcNow();
        await dbContext.Set<AuthSession>()
            .TagWithOperationCallSite("auth.sessions.revoke_others")
            .Where(s => s.UserId == userId && s.Id != keepSessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.RevokedAt, now)
                .SetProperty(s => s.RevokedReason, RevokeReason.RevokeOthers), CancellationToken.None);
    }

    /// <summary>Ends each web session, native session, and personal access token of the user, as a new temporary password does (PWD-14).</summary>
    public async Task RevokeAllAsync(string userId, RevokeReason reason)
    {
        var now = timeProvider.GetUtcNow();
        await dbContext.Set<AuthSession>()
            .TagWithOperationCallSite("auth.sessions.revoke_all")
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.RevokedAt, now)
                .SetProperty(s => s.RevokedReason, reason), CancellationToken.None);
    }

    public async Task AddAttemptAsync(LoginAttempt attempt, CancellationToken cancellationToken)
    {
        dbContext.Set<LoginAttempt>().Add(attempt);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<LoginAttempt?> FindAttemptAsync(TokenHash state, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await dbContext.Set<LoginAttempt>()
            .AsNoTracking()
            .TagWithOperationCallSite("auth.attempts.find")
            .FirstOrDefaultAsync(a => a.StateHash == state.Hex && a.ExpiresAt > now, cancellationToken);
    }

    public async Task<bool> ConsumeAttemptAsync(LoginAttempt attempt, CancellationToken cancellationToken)
    {
        var deleted = await dbContext.Set<LoginAttempt>()
            .TagWithOperationCallSite("auth.attempts.consume")
            .Where(a => a.StateHash == attempt.StateHash)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 1;
    }

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

    public async Task<AppCode?> FindAppCodeAsync(TokenHash code, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await dbContext.Set<AppCode>()
            .AsNoTracking()
            .TagWithOperationCallSite("auth.app_codes.find")
            .FirstOrDefaultAsync(c => c.CodeHash == code.Hex && c.ExpiresAt > now, cancellationToken);
    }

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
