namespace Recall.Server.Resources.Storage;

using Databases.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;

public static class FileStoreRegistration
{
    /// <summary>
    /// Adds <see cref="FileStore"/>, its mount check and cleanup at each start of the host, and its health check. A failed
    /// check gives Degraded, so <c>/health</c> stays 200 with the body <c>Degraded</c> and reads keep working.
    /// </summary>
    public static void AddFileStore(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<FileStoreOptions>()
            .BindConfiguration(FileStoreOptions.SectionName)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Root) && Path.IsPathFullyQualified(o.Root),
                $"{FileStoreOptions.SectionName}:Root must be a full path. Set the variable {FileStoreOptions.SectionName}__Root to the folder that holds the files.")
            .ValidateOnStart();
        builder.Services.AddSingleton<FileStore>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ReadMounts>(NetworkMountCheck.ReadMountsAsync);
        builder.Services.AddHostedService<FileStoreStartup>();
        builder.Services.AddSingleton<FileStoreHealthCheck>();
        builder.Services.AddHealthChecks().AddCheck<FileStoreHealthCheck>("file-store", HealthStatus.Degraded);
    }

    private sealed class FileStoreStartup(FileStore fileStore, ReadMounts readMounts) : IHostedService
    {
        private readonly ILogger _logger = Log.ForContext<FileStoreStartup>();

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var trace = Telemetry.TraceRoot([], "file_store.prepare");
            if (await readMounts(cancellationToken) is { } mounts && fileStore.MountRefusal(mounts) is { } refusal)
            {
                var exception = new InvalidOperationException(refusal);
                Telemetry.RecordError(trace.Activity, exception);
                throw exception;
            }

            var preparation = fileStore.Prepare();
            trace.Activity?.SetTag("app.file_store.writable", preparation.NotWritableCause is null);
            if (preparation.RemovedItems > 0)
                _logger.Here().Warning("Removed {TemporaryItemCount} temporary items of interrupted file store work", preparation.RemovedItems);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
