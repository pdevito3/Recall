namespace Recall.FunctionalTests.Auth;

using System.Diagnostics;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Databases.Sqlite;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

// Each test holds the writer line of the host, so the tests of this class take turns.
[NotInParallel(nameof(WriterLineTests))]
public class WriterLineTests
{
    private const string Me = "/auth/me";
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
