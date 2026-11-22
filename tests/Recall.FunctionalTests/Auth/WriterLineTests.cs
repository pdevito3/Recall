namespace Recall.FunctionalTests.Auth;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Databases.Sqlite;
using Recall.Server.Resources;
using Recall.Server.Resources.Entrypoints.Auth;
using Serilog.Events;
using Shouldly;

// Each test holds the writer line of the host, so the tests of this class take turns.
[NotInParallel(nameof(WriterLineTests))]
public class WriterLineTests
{
    private const string Me = "/auth/me";
    private const string SlideSpan = "session.slide";
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(1);

    private static AuthFlowFactory Host => AuthHosts.WriterLine;

    [Test]
    public async Task a_logout_whose_client_leaves_while_it_waits_in_the_writer_line_still_ends_the_session()
    {
        // Arrange
        using var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync($"subject-{Guid.NewGuid():N}");
        using var otherTab = new AuthBrowser(Host);
        otherTab.SetCookie(browser.SessionCookieName, browser.SessionCookie!);
        await using var held = await HeldLineAsync();
        using var leave = new CancellationTokenSource();

        // Act
        var logout = browser.PostFormAsync("/auth/logout", [], csrf: true, cancellationToken: leave.Token);
        await held.WaitForWritersAsync(2);
        // The test server aborts the request at once, but it ends the call of the client only when the server is done.
        await leave.CancelAsync();
        held.Release();
        await Should.ThrowAsync<OperationCanceledException>(() => logout.WaitAsync(Limit));
        await held.WaitForWritersAsync(0);

        // Assert
        (await otherTab.GetAsync(Me, csrf: true)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task me_with_a_slide_due_answers_while_a_write_holds_the_line()
    {
        // Arrange
        using var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync($"subject-{Guid.NewGuid():N}");
        AuthHosts.WriterLineClock.Advance(TimeSpan.FromDays(2));
        await using var held = await HeldLineAsync();
        var watch = Stopwatch.StartNew();

        // Act
        var me = await browser.GetAsync(Me, csrf: true).WaitAsync(Limit);

        // Assert
        watch.Elapsed.ShouldBeLessThan(AuthEndpoints.SlideWait + TimeSpan.FromSeconds(5));
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        await held.WaitForWritersAsync(1, AuthEndpoints.SlideWait + TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task me_with_a_slide_due_answers_while_the_write_of_its_slide_is_stuck()
    {
        // Arrange
        using var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync($"subject-{Guid.NewGuid():N}");
        AuthHosts.WriterLineClock.Advance(TimeSpan.FromDays(2));
        // The slide gets the turn, and its write then blocks in SQLite, as a write on a disk that hangs does.
        await using var outside = await WriteLockOutsideTheLineAsync();
        var watch = Stopwatch.StartNew();

        // Act
        var me = await browser.GetAsync(Me, csrf: true).WaitAsync(Limit);

        // Assert
        watch.Elapsed.ShouldBeLessThan(AuthEndpoints.SlideWait + TimeSpan.FromSeconds(5));
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task me_with_a_slide_due_moves_the_end_of_the_session_and_of_its_cookie()
    {
        // Arrange
        using var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync($"subject-{Guid.NewGuid():N}");
        AuthHosts.WriterLineClock.Advance(TimeSpan.FromDays(2));
        var now = AuthHosts.WriterLineClock.GetUtcNow();

        // Act
        var me = await browser.GetAsync(Me, csrf: true).WaitAsync(Limit);

        // Assert
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessionId = await SessionIdOfAsync(me);
        var session = await Host.WithDatabaseAsync(db => db.Set<AuthSession>().AsNoTracking().SingleAsync(s => s.Id == sessionId));
        session.ExpiresAt.ShouldBe(now + SessionPolicy.WebIdle, TimeSpan.FromMilliseconds(1));
        session.LastSlidAt.ShouldNotBeNull().ShouldBe(now, TimeSpan.FromMilliseconds(1));
        var cookie = SetCookieHeaderValue.Parse(me.Headers.GetValues("Set-Cookie").Single(line => line.StartsWith($"{browser.SessionCookieName}=", StringComparison.Ordinal)));
        cookie.Expires.ShouldNotBeNull().ShouldBe(now + SessionPolicy.WebIdle, TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task a_slide_that_fails_after_me_answered_writes_a_warning_and_fails_its_span()
    {
        // Arrange
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.ServiceName,
            Sample = (ref options) => options.Name == SlideSpan ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == SlideSpan)
                    spans.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var browser = new AuthBrowser(Host);
        await browser.LoginWebAsync($"subject-{Guid.NewGuid():N}");
        AuthHosts.WriterLineClock.Advance(TimeSpan.FromDays(2));
        // The slide gets the turn, and its write then waits for this lock until the limit of the writer line ends.
        await using var outside = await WriteLockOutsideTheLineAsync();

        // Act
        var me = await browser.GetAsync(Me, csrf: true).WaitAsync(Limit);
        var sessionId = await SessionIdOfAsync(me);
        // Each host writes the lines of the static logger to the sinks of Sweep. See AuthHosts.Start. Late in a run Sweep holds
        // millions of lines, and Holding renders each line, so a look at all of them took longer than the limit (#165).
        var warning = await EventuallyAsync(() => AuthHosts.Sweep.Logs.All
            .Where(line => line.Level == LogEventLevel.Warning && line.MessageTemplate.Text.Contains("failed after /auth/me answered", StringComparison.Ordinal))
            .SingleOrDefault(line => line.Properties["SessionId"] is ScalarValue { Value: string id } && id == sessionId));

        // Assert
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        warning.Exception.ShouldNotBeNull();
        var span = spans.Single(span => (string?)span.GetTagItem("app.session_id") == sessionId);
        span.Status.ShouldBe(ActivityStatusCode.Error);
    }

    private static async Task<string> SessionIdOfAsync(HttpResponseMessage me)
        => (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sid").GetString()!;

    private static async Task<T> EventuallyAsync<T>(Func<T?> find)
        where T : class
    {
        using var limit = new CancellationTokenSource(Limit);
        while (true)
        {
            if (find() is { } found)
                return found;
            await Task.Delay(TimeSpan.FromMilliseconds(20), limit.Token);
        }
    }

    private static async Task<SqliteConnection> WriteLockOutsideTheLineAsync()
    {
        await using var scope = Host.Services.CreateAsyncScope();
        // A pooled connection keeps its open transaction in the pool, and the host would then hold the lock for later tests.
        var connectionString = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var begin = new SqliteCommand("BEGIN IMMEDIATE", connection);
        await begin.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<HeldLine> HeldLineAsync()
    {
        var scope = Host.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database;
        var turn = await SqliteWriterLine.WaitForTurnAsync(database, CancellationToken.None);
        return new HeldLine(scope, turn, database.GetConnectionString()!);
    }

    private sealed class HeldLine(AsyncServiceScope scope, SqliteWriterLine.Turn turn, string connectionString) : IAsyncDisposable
    {
        private bool _released;

        public int Writers => SqliteWriterLine.WritersInLineWithTurnHolder(connectionString);

        public void Release()
        {
            if (_released)
                return;
            _released = true;
            turn.Dispose();
        }

        public async Task WaitForWritersAsync(int count, TimeSpan? within = null)
        {
            using var limit = new CancellationTokenSource(within ?? Limit);
            while (Writers != count)
                await Task.Delay(TimeSpan.FromMilliseconds(5), limit.Token);
        }

        public async ValueTask DisposeAsync()
        {
            Release();
            await scope.DisposeAsync();
        }
    }
}
