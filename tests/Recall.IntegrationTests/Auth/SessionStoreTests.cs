namespace Recall.IntegrationTests.Auth;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public sealed class SessionStoreTests : TestBase
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private const string Mode = "oidc";

    private string UserId { get; set; } = null!;

    [Before(Test)]
    public async Task AddUserOfThisTest()
    {
        await using var scope = TestFixture.BaseScopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = User.CreateFromLogin(new NewPerson("Ada", null, null, "ada"), TestFixture.DefaultTenant.Id, UserRole.User());
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        UserId = user.Id;
    }

    private sealed class Host : IAsyncDisposable
    {
        public FakeTimeProvider Clock { get; } = new(Start);
        public ServiceProvider Services { get; }

        public Host(params IInterceptor[] interceptors)
            => Services = TestFixture.BuildServiceProvider(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                if (interceptors.Length > 0)
                    services.ConfigureDbContext<AppDbContext>((_, options) => options.AddInterceptors(interceptors));
            }, TestFixture.UnpooledConnectionString);

        public Host(AuthTestDatabase database)
            => Services = database.BuildServices(configure: services => services.AddSingleton<TimeProvider>(Clock));

        public async Task<T> WithStoreAsync<T>(Func<SessionStore, Task<T>> work)
        {
            await using var scope = Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            return await work(scope.ServiceProvider.GetRequiredService<SessionStore>());
        }

        public async Task<AuthSession?> RowAsync(TokenPair pair)
        {
            await using var scope = Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            RefreshToken.TryParse(pair.RefreshToken, out var token).ShouldBeTrue();
            var family = TokenHash.Of(token!.Family).Hex;
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<AuthSession>().AsNoTracking()
                .SingleOrDefaultAsync(s => s.RefreshFamilyHash == family);
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private sealed class CommandLog : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static RefreshToken Parse(string raw)
    {
        RefreshToken.TryParse(raw, out var token).ShouldBeTrue();
        return token!;
    }

    private static RetryKey Key()
    {
        RetryKey.TryParse(Secrets.New(), out var key).ShouldBeTrue();
        return key!.Value;
    }

    [Test]
    public async Task the_hot_path_is_one_read_and_no_write()
    {
        // Arrange
        var log = new CommandLog();
        await using var host = new Host(log);
        var pair = await host.WithStoreAsync(store => store.StartNativeAsync(UserId, Mode, CancellationToken.None));
        log.Commands.Clear();

        // Act
        var principal = await host.WithStoreAsync(store => store.FindByAccessAsync(TokenHash.Of(pair.AccessToken), SessionKind.Native, CancellationToken.None));

        // Assert
        principal!.UserId.ShouldBe(UserId);
        var command = log.Commands.Where(c => !c.Contains("PRAGMA", StringComparison.OrdinalIgnoreCase)).ShouldHaveSingleItem();
        command.ShouldContain("auth.sessions.find_by_access");
        command.ShouldContain("SELECT");
        command.ShouldNotContain("UPDATE");
    }

    [Test]
    public async Task a_credential_works_only_for_its_kind_while_it_is_live()
    {
        // Arrange
        await using var host = new Host();
        var web = await host.WithStoreAsync(store => store.StartWebAsync(UserId, Mode, CancellationToken.None));
        var native = await host.WithStoreAsync(store => store.StartNativeAsync(UserId, Mode, CancellationToken.None));
        Task<SessionPrincipal?> Find(string value, SessionKind kind)
            => host.WithStoreAsync(store => store.FindByAccessAsync(TokenHash.Of(value), kind, CancellationToken.None));

        // Act
        var cookieAsWeb = await Find(web.CookieValue, SessionKind.Web);
        var cookieAsNative = await Find(web.CookieValue, SessionKind.Native);
        var bearerAsNative = await Find(native.AccessToken, SessionKind.Native);
        var bearerAsWeb = await Find(native.AccessToken, SessionKind.Web);
        host.Clock.Advance(SessionPolicy.BearerLifetime);
        var expiredBearer = await Find(native.AccessToken, SessionKind.Native);
        var cookieLater = await Find(web.CookieValue, SessionKind.Web);

        // Assert
        cookieAsWeb.ShouldNotBeNull();
        cookieAsNative.ShouldBeNull();
        bearerAsNative.ShouldNotBeNull();
        bearerAsWeb.ShouldBeNull();
        expiredBearer.ShouldBeNull();
        cookieLater.ShouldNotBeNull();
    }

    [Test]
    public async Task rotation_gives_a_new_pair_slides_the_session_and_kills_the_old_access_token()
    {
        // Arrange
        await using var host = new Host();
        var first = await host.WithStoreAsync(store => store.StartNativeAsync(UserId, Mode, CancellationToken.None));
        host.Clock.Advance(TimeSpan.FromMinutes(5));

        // Act
        var result = await host.WithStoreAsync(store => store.RefreshAsync(Parse(first.RefreshToken), null, CancellationToken.None));

        // Assert
        result.Decision.ShouldBe(RefreshDecision.Rotate);
        var pair = result.Pair!;
        pair.AccessToken.ShouldNotBe(first.AccessToken);
        pair.RefreshToken.ShouldNotBe(first.RefreshToken);
        Parse(pair.RefreshToken).Family.ShouldBe(Parse(first.RefreshToken).Family);
        pair.AccessExpiresAt.ShouldBe(host.Clock.GetUtcNow() + SessionPolicy.BearerLifetime);
        (await host.WithStoreAsync(store => store.FindByAccessAsync(TokenHash.Of(first.AccessToken), SessionKind.Native, CancellationToken.None))).ShouldBeNull();
        (await host.WithStoreAsync(store => store.FindByAccessAsync(TokenHash.Of(pair.AccessToken), SessionKind.Native, CancellationToken.None))).ShouldNotBeNull();
        var row = await host.RowAsync(pair);
        row!.ExpiresAt.ShouldBe(host.Clock.GetUtcNow() + SessionPolicy.RefreshIdle);
        row.RefreshHash.ShouldBe(TokenHash.Of(pair.RefreshToken).Hex);
    }

    [Test]
    public async Task a_replay_revokes_the_whole_session()
    {
        // Arrange
        await using var host = new Host();
        var first = await host.WithStoreAsync(store => store.StartNativeAsync(UserId, Mode, CancellationToken.None));
        var second = (await host.WithStoreAsync(store => store.RefreshAsync(Parse(first.RefreshToken), null, CancellationToken.None))).Pair!;

        // Act
        var replay = await host.WithStoreAsync(store => store.RefreshAsync(Parse(first.RefreshToken), null, CancellationToken.None));

        // Assert
        replay.Pair.ShouldBeNull();
        replay.Decision.ShouldBe(RefreshDecision.Replay);
        var row = await host.RowAsync(second);
        row!.RevokedAt.ShouldNotBeNull();
        row.RevokedReason.ShouldBe(RevokeReason.RefreshReplay);
        (await host.WithStoreAsync(store => store.FindByAccessAsync(TokenHash.Of(second.AccessToken), SessionKind.Native, CancellationToken.None))).ShouldBeNull();
        var currentAfter = await host.WithStoreAsync(store => store.RefreshAsync(Parse(second.RefreshToken), null, CancellationToken.None));
        currentAfter.Pair.ShouldBeNull();
        currentAfter.Decision.ShouldBe(RefreshDecision.Revoked);
    }

    [Test]
    public async Task a_family_with_no_row_is_refused_with_no_write()
    {
        // Arrange
        var log = new CommandLog();
        await using var host = new Host(log);

        // Act
        var result = await host.WithStoreAsync(store => store.RefreshAsync(RefreshToken.New(), null, CancellationToken.None));

        // Assert
        result.Pair.ShouldBeNull();
        log.Commands.ShouldAllBe(c => !c.Contains("UPDATE", StringComparison.Ordinal));
    }

    [Test]
    public async Task a_retry_key_gives_the_same_pair_across_two_lost_responses_with_no_write_and_no_revoke()
    {
        // Arrange
        var log = new CommandLog();
        await using var host = new Host(log);
        var first = await host.WithStoreAsync(store => store.StartNativeAsync(UserId, Mode, CancellationToken.None));
        var key = Key();

        // Act
        var rotated = await host.WithStoreAsync(store => store.RefreshAsync(Parse(first.RefreshToken), key, CancellationToken.None));
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        log.Commands.Clear();
        var lostOnce = await host.WithStoreAsync(store => store.RefreshAsync(Parse(first.RefreshToken), key, CancellationToken.None));
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        var lostTwice = await host.WithStoreAsync(store => store.RefreshAsync(Parse(first.RefreshToken), key, CancellationToken.None));

        // Assert
        rotated.Decision.ShouldBe(RefreshDecision.Rotate);
        lostOnce.Decision.ShouldBe(RefreshDecision.RotateRetry);
        lostTwice.Decision.ShouldBe(RefreshDecision.RotateRetry);
        lostOnce.Pair!.RefreshToken.ShouldBe(rotated.Pair!.RefreshToken);
        lostOnce.Pair.AccessToken.ShouldBe(rotated.Pair.AccessToken);
        lostTwice.Pair!.RefreshToken.ShouldBe(rotated.Pair.RefreshToken);
        lostTwice.Pair.AccessExpiresAt.ShouldBe(rotated.Pair.AccessExpiresAt);
        log.Commands.ShouldAllBe(c => !c.Contains("UPDATE", StringComparison.Ordinal));
        (await host.RowAsync(rotated.Pair))!.RevokedAt.ShouldBeNull();
        (await host.WithStoreAsync(store => store.FindByAccessAsync(TokenHash.Of(rotated.Pair.AccessToken), SessionKind.Native, CancellationToken.None))).ShouldNotBeNull();
    }

    [Test]
    public async Task a_rival_copy_that_commits_first_gives_the_same_pair_through_the_second_read()
    {
        // Arrange
        var pause = PauseAtQuery.After("auth.sessions.refresh_read");
        await using var held = new Host(pause);
        await using var rivalHost = new Host();
        var first = await rivalHost.WithStoreAsync(store => store.StartNativeAsync(UserId, Mode, CancellationToken.None));
        var key = Key();

        // Act
        var heldCopy = held.WithStoreAsync(store => store.RefreshAsync(Parse(first.RefreshToken), key, CancellationToken.None));
        await pause.ReachedAsync();
        var rival = await rivalHost.WithStoreAsync(store => store.RefreshAsync(Parse(first.RefreshToken), key, CancellationToken.None));
        pause.Release();
        var result = await heldCopy;

        // Assert
        rival.Decision.ShouldBe(RefreshDecision.Rotate);
        result.Decision.ShouldBe(RefreshDecision.RotateRetry);
        result.Pair!.RefreshToken.ShouldBe(rival.Pair!.RefreshToken);
        result.Pair.AccessToken.ShouldBe(rival.Pair.AccessToken);
        (await rivalHost.RowAsync(rival.Pair))!.RevokedAt.ShouldBeNull();
    }

    [Test]
    public async Task two_rivals_with_no_key_end_in_one_winner_and_a_replay()
    {
        // Arrange
        var pause = PauseAtQuery.After("auth.sessions.refresh_read");
        await using var held = new Host(pause);
        await using var rivalHost = new Host();
        var first = await rivalHost.WithStoreAsync(store => store.StartNativeAsync(UserId, Mode, CancellationToken.None));

        // Act
        var heldCopy = held.WithStoreAsync(store => store.RefreshAsync(Parse(first.RefreshToken), null, CancellationToken.None));
        await pause.ReachedAsync();
        var rival = await rivalHost.WithStoreAsync(store => store.RefreshAsync(Parse(first.RefreshToken), null, CancellationToken.None));
        pause.Release();
        var result = await heldCopy;

        // Assert
        rival.Pair.ShouldNotBeNull();
        result.Decision.ShouldBe(RefreshDecision.Replay);
        (await rivalHost.RowAsync(rival.Pair))!.RevokedReason.ShouldBe(RevokeReason.RefreshReplay);
    }

    [Test]
    public async Task an_expired_session_is_refused_with_no_revoke()
    {
        // Arrange
        await using var host = new Host();
        var first = await host.WithStoreAsync(store => store.StartNativeAsync(UserId, Mode, CancellationToken.None));
        host.Clock.Advance(SessionPolicy.RefreshIdle);

        // Act
        var result = await host.WithStoreAsync(store => store.RefreshAsync(Parse(first.RefreshToken), null, CancellationToken.None));

        // Assert
        result.Pair.ShouldBeNull();
        result.Decision.ShouldBe(RefreshDecision.Expired);
        (await host.RowAsync(first))!.RevokedAt.ShouldBeNull();
    }

    [Test]
    public async Task a_web_session_slides_at_most_once_a_day_and_never_past_the_absolute_limit()
    {
        // Arrange
        await using var host = new Host();
        var web = await host.WithStoreAsync(store => store.StartWebAsync(UserId, Mode, CancellationToken.None));
        Task<DateTimeOffset?> Slide() => host.WithStoreAsync(store => store.SlideWebAsync(web.SessionId, CancellationToken.None));

        // Act
        var tooSoon = await Slide();
        host.Clock.Advance(SessionPolicy.WebSlideEvery);
        var slid = await Slide();
        var sameDay = await Slide();
        host.Clock.Advance(TimeSpan.FromDays(29));
        for (var day = 0; day < 5; day++)
        {
            (await Slide()).ShouldNotBeNull();
            host.Clock.Advance(TimeSpan.FromDays(29));
        }
        var capped = await Slide();
        host.Clock.Advance(TimeSpan.FromDays(1));
        var afterTheCap = await Slide();

        // Assert
        tooSoon.ShouldBeNull();
        slid.ShouldBe(Start + SessionPolicy.WebSlideEvery + SessionPolicy.WebIdle);
        sameDay.ShouldBeNull();
        capped.ShouldBe(Start + SessionPolicy.WebAbsolute);
        afterTheCap.ShouldBeNull();
    }

    [Test]
    public async Task revoke_others_keeps_only_the_named_session_of_that_user()
    {
        // Arrange
        await using var host = new Host();
        var kept = await host.WithStoreAsync(store => store.StartWebAsync(UserId, Mode, CancellationToken.None));
        var otherWeb = await host.WithStoreAsync(store => store.StartWebAsync(UserId, Mode, CancellationToken.None));
        var otherNative = await host.WithStoreAsync(store => store.StartNativeAsync(UserId, Mode, CancellationToken.None));

        // Act
        await host.WithStoreAsync(async store =>
        {
            await store.RevokeOthersAsync(UserId, kept.SessionId, CancellationToken.None);
            return true;
        });

        // Assert
        (await host.WithStoreAsync(store => store.FindByAccessAsync(TokenHash.Of(kept.CookieValue), SessionKind.Web, CancellationToken.None))).ShouldNotBeNull();
        (await host.WithStoreAsync(store => store.FindByAccessAsync(TokenHash.Of(otherWeb.CookieValue), SessionKind.Web, CancellationToken.None))).ShouldBeNull();
        (await host.RowAsync(otherNative))!.RevokedReason.ShouldBe(RevokeReason.RevokeOthers);
    }

    [Test]
    public async Task an_old_token_of_the_family_is_enough_to_end_a_session()
    {
        // Arrange
        await using var host = new Host();
        var first = await host.WithStoreAsync(store => store.StartNativeAsync(UserId, Mode, CancellationToken.None));
        var second = (await host.WithStoreAsync(store => store.RefreshAsync(Parse(first.RefreshToken), null, CancellationToken.None))).Pair!;

        // Act
        await host.WithStoreAsync(async store =>
        {
            await store.RevokeByRefreshAsync(Parse(first.RefreshToken), CancellationToken.None);
            return true;
        });

        // Assert
        (await host.RowAsync(second))!.RevokedReason.ShouldBe(RevokeReason.Logout);
    }

    private static LoginAttempt Attempt(string state, DateTimeOffset expiresAt) => new()
    {
        StateHash = TokenHash.Of(state).Hex,
        BindingHash = TokenHash.Of(Secrets.New()).Hex,
        ModeId = Mode,
        Nonce = Secrets.New(),
        PkceVerifier = Secrets.New(),
        RedirectUri = "http://localhost/auth/oidc/callback",
        ClientKind = SessionKind.Web,
        ReturnPath = "/",
        ExpiresAt = expiresAt,
    };

    [Test]
    public async Task an_attempt_is_consumed_once_under_concurrency()
    {
        // Arrange
        await using var host = new Host();
        var state = Secrets.New();
        await host.WithStoreAsync(async store =>
        {
            await store.AddAttemptAsync(Attempt(state, Start.AddMinutes(10)), CancellationToken.None);
            return true;
        });
        var attempt = await host.WithStoreAsync(store => store.FindAttemptAsync(TokenHash.Of(state), CancellationToken.None));

        // Act
        var wins = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => host.WithStoreAsync(store => store.ConsumeAttemptAsync(attempt!, CancellationToken.None)))));

        // Assert
        wins.Count(won => won).ShouldBe(1);
        (await host.WithStoreAsync(store => store.FindAttemptAsync(TokenHash.Of(state), CancellationToken.None))).ShouldBeNull();
    }

    [Test]
    public async Task an_expired_attempt_is_not_found()
    {
        // Arrange
        await using var host = new Host();
        var state = Secrets.New();
        await host.WithStoreAsync(async store =>
        {
            await store.AddAttemptAsync(Attempt(state, Start.AddMinutes(10)), CancellationToken.None);
            return true;
        });

        // Act
        var live = await host.WithStoreAsync(store => store.FindAttemptAsync(TokenHash.Of(state), CancellationToken.None));
        host.Clock.Advance(TimeSpan.FromMinutes(10));
        var expired = await host.WithStoreAsync(store => store.FindAttemptAsync(TokenHash.Of(state), CancellationToken.None));

        // Assert
        live.ShouldNotBeNull();
        expired.ShouldBeNull();
    }

    [Test]
    public async Task an_app_code_is_consumed_once_under_concurrency()
    {
        // Arrange
        await using var host = new Host();
        var code = await host.WithStoreAsync(store => store.AddAppCodeAsync(UserId, Mode, "recall://auth/callback", "challenge", "state", CancellationToken.None));

        // Act
        var found = await host.WithStoreAsync(store => store.FindAppCodeAsync(TokenHash.Of(code), CancellationToken.None));
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => host.WithStoreAsync(store => store.ConsumeAppCodeAsync(TokenHash.Of(code), CancellationToken.None)))));

        // Assert
        found.ShouldNotBeNull();
        results.Count(r => r is not null).ShouldBe(1);
        (await host.WithStoreAsync(store => store.ConsumeAppCodeAsync(TokenHash.Of(code), CancellationToken.None))).ShouldBeNull();
    }

    [Test]
    public async Task an_expired_app_code_is_refused()
    {
        // Arrange
        await using var host = new Host();
        var code = await host.WithStoreAsync(store => store.AddAppCodeAsync(UserId, Mode, "recall://auth/callback", "challenge", null, CancellationToken.None));
        host.Clock.Advance(SessionPolicy.AppCodeLifetime);

        // Act
        var result = await host.WithStoreAsync(store => store.ConsumeAppCodeAsync(TokenHash.Of(code), CancellationToken.None));

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public async Task the_sweep_deletes_what_ended_and_keeps_what_is_live()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        await using var host = new Host(database);
        var login = await host.Services.GetRequiredService<Provisioner>().ResolveAsync(
            new ExternalIdentity("https://a.example", "swept", new Profile("Ada", null, "Ada", null, "ada")), CancellationToken.None);
        var userId = login.ShouldBeOfType<ProvisionOutcome.Provisioned>().UserId;
        var expiredCode = await host.WithStoreAsync(store => store.AddAppCodeAsync(userId, Mode, "recall://auth/callback", "challenge", null, CancellationToken.None));
        var expiredState = Secrets.New();
        await host.WithStoreAsync(async store =>
        {
            await store.AddAttemptAsync(Attempt(expiredState, Start.AddMinutes(10)), CancellationToken.None);
            return true;
        });
        var revoked = await host.WithStoreAsync(store => store.StartWebAsync(userId, Mode, CancellationToken.None));
        await host.WithStoreAsync(async store =>
        {
            await store.RevokeAsync(revoked.SessionId, RevokeReason.Logout, CancellationToken.None);
            return true;
        });
        var native = await host.WithStoreAsync(store => store.StartNativeAsync(userId, Mode, CancellationToken.None));
        host.Clock.Advance(SessionPolicy.KeepEndedSession + TimeSpan.FromDays(1));
        var liveState = Secrets.New();
        await host.WithStoreAsync(async store =>
        {
            await store.AddAttemptAsync(Attempt(liveState, host.Clock.GetUtcNow().AddMinutes(10)), CancellationToken.None);
            return true;
        });
        var liveCode = await host.WithStoreAsync(store => store.AddAppCodeAsync(userId, Mode, "recall://auth/callback", "challenge", null, CancellationToken.None));

        // Act
        await host.WithStoreAsync(async store =>
        {
            await store.SweepAsync(CancellationToken.None);
            return true;
        });

        // Assert
        await using var scope = host.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expiredStateHash = TokenHash.Of(expiredState).Hex;
        var liveStateHash = TokenHash.Of(liveState).Hex;
        var expiredCodeHash = TokenHash.Of(expiredCode).Hex;
        var liveCodeHash = TokenHash.Of(liveCode).Hex;
        (await db.Set<LoginAttempt>().CountAsync(a => a.StateHash == expiredStateHash)).ShouldBe(0);
        (await db.Set<LoginAttempt>().CountAsync(a => a.StateHash == liveStateHash)).ShouldBe(1);
        (await db.Set<AppCode>().CountAsync(c => c.CodeHash == expiredCodeHash)).ShouldBe(0);
        (await db.Set<AppCode>().CountAsync(c => c.CodeHash == liveCodeHash)).ShouldBe(1);
        (await db.Set<AuthSession>().CountAsync(s => s.Id == revoked.SessionId)).ShouldBe(0);
        (await host.RowAsync(native)).ShouldNotBeNull();
    }
}
