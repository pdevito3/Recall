namespace Recall.Server.Databases;

using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;
using Serilog.Core;
using Sqlite;

public class MigrationHostedService<TDbContext>(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    WalScrub walScrub)
    : IHostedService
    where TDbContext : DbContext
{
    // ForContext<T> names a generic type with its assembly-qualified arguments, which fills a console line.
    private readonly ILogger _logger = Log.ForContext(
        Constants.SourceContextPropertyName, $"{typeof(MigrationHostedService<>).Namespace}.MigrationHostedService");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Startup has no request to hang this work on. Without a root, each migration query exports as
        // its own rootless trace. With one, the whole run is a single trace named after what it does.
        using var trace = Telemetry.TraceRoot(
            [("app.db_context", typeof(TDbContext).Name)],
            "database.migrate");

        try
        {
            _logger.Here().Information("Applying migrations for {DbContext}", typeof(TDbContext).Name);

            var connectionString = configuration.GetConnectionString(DatabaseConsts.DatabaseName);

            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.Here().Warning(
                    "No connection string found for {DatabaseName}. Skipping migrations",
                    DatabaseConsts.DatabaseName);
                return;
            }

            await SqliteFile.PrepareAsync(connectionString, cancellationToken);

            await using var scope = scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TDbContext>();
            var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
            if (NewerVersionRefusal(applied, context.Database.GetMigrations()) is { } refusal)
                throw new InvalidOperationException(refusal);

            await context.Database.MigrateAsync(cancellationToken);

            // A kill between the commit of a delete and its scrub leaves the deleted words in the WAL. This runs before Kestrel binds.
            var scrubbed = await walScrub.TruncateAsync(connectionString);
            if (!scrubbed)
                _logger.Here().Warning("Could not truncate the WAL at startup, so deleted words can stay in the WAL until a retry passes");

            _logger.Here().Information("Migrations complete for {DbContext}, WAL scrubbed {WalScrubbed}", typeof(TDbContext).Name, scrubbed);
        }
        catch (Exception ex)
        {
            Telemetry.RecordError(trace.Activity, ex);
            _logger.Here().Error(ex, "An error occurred while applying the database migrations.");
            throw;
        }
    }

    // EF applies only the migrations it knows and ignores the others, so an older binary would run on a schema it was not built for.
    private static string? NewerVersionRefusal(IEnumerable<string> applied, IEnumerable<string> known)
    {
        var unknown = applied.Except(known).ToList();
        return unknown.Count == 0
            ? null
            : $"The database holds migrations that this version of Recall does not know: {string.Join(", ", unknown)}. " +
              "A newer version changed the database, and this version cannot run on it. " +
              "Start the newer version again, or restore the backup that you made before the upgrade.";
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
