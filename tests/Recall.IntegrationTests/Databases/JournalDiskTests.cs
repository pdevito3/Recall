namespace Recall.IntegrationTests.Databases;

using System.Text;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Databases;
using Recall.Server.Domain.Corrections.Dtos;
using Recall.Server.Domain.Corrections.Features;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Features;
using Recall.Server.Domain.Summaries;
using Recall.Server.Domain.TimeZones;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Tenants.Features;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

// Each test has a database of its own and a quiet server, and it runs no checkpoint. So the handler alone takes the
// words out of the WAL, and a test that shares a database with other tests could not tell.
public class JournalDiskTests
{
    private static readonly string Words = string.Concat(Enumerable.Repeat("spoken words ", 500));

    [Test]
    [Property("Rule", "SES-27")]
    public async Task after_a_delete_of_a_session_the_words_are_not_in_the_database_file_or_the_wal()
    {
        // Arrange
        await using var server = await QuietServer.StartAsync();
        var marker = $"DISKMARKER{Guid.NewGuid():N}";
        var session = await server.SaveWordsAsync(marker);
        server.FilesHold(marker).ShouldBeTrue();

        // Act
        await server.AsOwner().SendAsync(new DeleteSession.Command(session));

        // Assert
        server.FilesHold(marker).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task after_a_delete_of_a_session_the_words_of_its_summaries_are_not_in_the_database_file_or_the_wal()
    {
        // Arrange
        await using var server = await QuietServer.StartAsync();
        var marker = $"DISKMARKER{Guid.NewGuid():N}";
        var session = await server.SaveWordsAsync(marker);
        await server.SaveWordsAsync("kept");
        await server.SaveSummariesAsync($"They wrote: {marker} {Words}");
        server.FilesHold(marker).ShouldBeTrue();

        // Act
        await server.AsOwner().SendAsync(new DeleteSession.Command(session));

        // Assert
        server.FilesHold(marker).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task after_a_delete_of_a_correction_the_words_are_not_in_the_database_file_or_the_wal()
    {
        // Arrange
        await using var server = await QuietServer.StartAsync();
        var marker = $"DISKMARKER{Guid.NewGuid():N}";
        var scope = server.AsOwner();
        var correction = await scope.SendAsync(new AddCorrection.Command(new CorrectionForCreationDto { CanonicalTerm = marker, Mishearings = [$"heard {marker}"] }));
        server.FilesHold(marker).ShouldBeTrue();

        // Act
        await server.AsOwner().SendAsync(new DeleteCorrection.Command(correction.Correction.Id, null));

        // Assert
        server.FilesHold(marker).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task after_a_delete_of_a_user_the_words_are_not_in_the_database_file_or_the_wal()
    {
        // Arrange
        await using var server = await QuietServer.StartAsync();
        var marker = $"DISKMARKER{Guid.NewGuid():N}";
        await server.SaveWordsAsync(marker);
        server.FilesHold(marker).ShouldBeTrue();

        // Act
        await server.AsAdmin().SendAsync(new DeleteUser.Command(server.Owner.Id));

        // Assert
        server.FilesHold(marker).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task after_a_delete_of_a_tenant_the_words_are_not_in_the_database_file_or_the_wal()
    {
        // Arrange
        await using var server = await QuietServer.StartAsync();
        var marker = $"DISKMARKER{Guid.NewGuid():N}";
        await server.SaveWordsAsync(marker);
        server.FilesHold(marker).ShouldBeTrue();

        // Act
        await server.AsAdmin().SendAsync(new DeleteTenant.Command(server.Tenant.Id));

        // Assert
        server.FilesHold(marker).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task a_client_abort_after_the_commit_of_a_delete_of_a_session_still_takes_the_words_out_of_the_wal()
    {
        // Arrange
        await using var server = await QuietServer.StartAsync();
        var marker = $"DISKMARKER{Guid.NewGuid():N}";
        var session = await server.SaveWordsAsync(marker);

        // Act
        await QuietServer.SendAndAbortAfterTheCommitAsync(server.AsOwner(), new DeleteSession.Command(session));

        // Assert
        server.FilesHold(marker).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task a_client_abort_after_the_commit_of_a_delete_of_a_correction_still_takes_the_words_out_of_the_wal()
    {
        // Arrange
        await using var server = await QuietServer.StartAsync();
        var marker = $"DISKMARKER{Guid.NewGuid():N}";
        var correction = await server.AsOwner().SendAsync(new AddCorrection.Command(new CorrectionForCreationDto { CanonicalTerm = marker, Mishearings = [$"heard {marker}"] }));

        // Act
        await QuietServer.SendAndAbortAfterTheCommitAsync(server.AsOwner(), new DeleteCorrection.Command(correction.Correction.Id, null));

        // Assert
        server.FilesHold(marker).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task a_client_abort_after_the_commit_of_a_delete_of_a_user_still_takes_the_words_out_of_the_wal()
    {
        // Arrange
        await using var server = await QuietServer.StartAsync();
        var marker = $"DISKMARKER{Guid.NewGuid():N}";
        await server.SaveWordsAsync(marker);

        // Act
        await QuietServer.SendAndAbortAfterTheCommitAsync(server.AsAdmin(), new DeleteUser.Command(server.Owner.Id));

        // Assert
        server.FilesHold(marker).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-27")]
    public async Task a_client_abort_after_the_commit_of_a_delete_of_a_tenant_still_takes_the_words_out_of_the_wal()
    {
        // Arrange
        await using var server = await QuietServer.StartAsync();
        var marker = $"DISKMARKER{Guid.NewGuid():N}";
        await server.SaveWordsAsync(marker);

        // Act
        await QuietServer.SendAndAbortAfterTheCommitAsync(server.AsAdmin(), new DeleteTenant.Command(server.Tenant.Id));

        // Assert
        server.FilesHold(marker).ShouldBeFalse();
    }

    private sealed class QuietServer : IAsyncDisposable
    {
        private readonly SqliteTestDatabase _database;
        private readonly ServiceProvider _services;

        private QuietServer(SqliteTestDatabase database, ServiceProvider services, Tenant tenant, User admin, User owner)
        {
            _database = database;
            _services = services;
            Tenant = tenant;
            Admin = admin;
            Owner = owner;
        }

        public Tenant Tenant { get; }

        public User Admin { get; }

        public User Owner { get; }

        private string Path => new SqliteConnectionStringBuilder(_database.ConnectionString).DataSource;

        public static async Task<QuietServer> StartAsync()
        {
            var database = new SqliteTestDatabase();
            var services = TestFixture.BuildServiceProvider(_ => { }, database.ConnectionString);
            await using (var system = services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope())
                await system.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

            TestGraph.Start(out _)
                .AddTenant(out var tenant)
                .AddUsers(out var users, u => u.Role = UserRole.Admin().Value, u => u.Role = UserRole.User().Value);

            // An install keeps a live tenant, so a delete of a tenant needs another one.
            TestGraph.Start(out _).AddTenant(out var otherTenant);
            await using (var system = services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope())
            {
                var dbContext = system.ServiceProvider.GetRequiredService<AppDbContext>();
                dbContext.Tenants.AddRange(tenant, otherTenant);
                dbContext.Users.AddRange(users);
                await dbContext.SaveChangesAsync();
            }

            return new QuietServer(database, services, tenant, users[0], users[1]);
        }

        public static async Task SendAndAbortAfterTheCommitAsync(TestingServiceScope scope, IRequest command)
        {
            using var abort = new CancellationTokenSource();
            scope.GetService<AppDbContext>().SavedChanges += (_, _) => abort.Cancel();
            await scope.GetService<ISender>().Send(command, abort.Token);
        }

        public TestingServiceScope AsOwner() => ScopeOf(Owner);

        public TestingServiceScope AsAdmin() => ScopeOf(Admin);

        public async Task<string> SaveWordsAsync(string marker)
        {
            var scope = AsOwner();
            var created = await scope.SendAsync(new AddSession.Command(new SessionForCreationDto
            {
                ClientSessionId = Guid.NewGuid().ToString(),
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            }));
            await scope.SendAsync(new SaveRawDraft.Command(created.Session.Id, new DraftSaveDto
            {
                Text = $"{marker} {Words}",
                BaseRevision = 0,
                SavedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            }));
            return created.Session.Id;
        }

        /// <summary>A ready Summary of each period of today, as a run would store it before a source went away.</summary>
        public async Task SaveSummariesAsync(string content)
        {
            var today = ZonedDate.TodayAt(TimeZoneId.Of("UTC"), DateTimeOffset.UtcNow.AddMinutes(-5)).Date;
            var dbContext = AsOwner().GetService<AppDbContext>();
            foreach (var period in Enum.GetValues<SummaryPeriod>())
            {
                var summary = Summary.WithFirstRun(Tenant.Id, Owner.Id, PeriodKey.Holding(period, today), jobId: null);
                summary.Complete(summary.RunId!, content, new SourceState(2, new string('a', Summary.FingerprintLength)), DateTimeOffset.UtcNow);
                dbContext.Summaries.Add(summary);
            }

            await dbContext.SaveChangesAsync();
        }

        public bool FilesHold(string marker)
        {
            var needle = Encoding.UTF8.GetBytes(marker);
            return new[] { Path, $"{Path}-wal" }.Any(file => Holds(file, needle));
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _database.DisposeAsync();
        }

        private TestingServiceScope ScopeOf(User user)
        {
            var scope = new TestingServiceScope(_services);
            scope.SetUserWithDefaults(identifier: user.Identifier);
            return scope;
        }

        private static bool Holds(string file, byte[] needle)
        {
            if (!File.Exists(file))
                return false;

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf(needle) >= 0;
        }
    }
}
