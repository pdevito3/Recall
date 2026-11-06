namespace Recall.Server.Databases;

using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;
using Serilog.Core;
using Sqlite;

public class MigrationHostedService<TDbContext>(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration)
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
            await context.Database.MigrateAsync(cancellationToken);

            _logger.Here().Information("Migrations complete for {DbContext}", typeof(TDbContext).Name);
        }
        catch (Exception ex)
        {
            Telemetry.RecordError(trace.Activity, ex);
            _logger.Here().Error(ex, "An error occurred while applying the database migrations.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
