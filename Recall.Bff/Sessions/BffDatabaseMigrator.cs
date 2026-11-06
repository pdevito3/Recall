using Microsoft.EntityFrameworkCore;
using Recall.Server.Databases.Sqlite;
using Serilog;
using ILogger = Serilog.ILogger;

namespace Recall.Bff.Sessions;

public sealed class BffDatabaseMigrator(IServiceScopeFactory scopeFactory) : IHostedService
{
    private static readonly ILogger Logger = Log.ForContext<BffDatabaseMigrator>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var activity = Telemetry.Source.StartActivity("database.migrate");
        activity?.SetTag("app.db_context", nameof(BffDbContext));
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<BffDbContext>();
            await SqliteFile.PrepareAsync(dbContext.Database.GetConnectionString()!, cancellationToken);
            await dbContext.Database.MigrateAsync(cancellationToken);
            Logger.Here().Information("Migrations complete for {DbContext}", nameof(BffDbContext));
        }
        catch (Exception ex)
        {
            Telemetry.RecordError(activity, ex);
            Logger.Here().Error(ex, "Could not apply the migrations of {DbContext}", nameof(BffDbContext));
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
