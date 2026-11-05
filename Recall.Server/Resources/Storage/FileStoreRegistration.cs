namespace Recall.Server.Resources.Storage;

using Serilog;

public static class FileStoreRegistration
{
    /// <summary>Adds <see cref="FileStore"/>, and its cleanup at each start of the host.</summary>
    public static void AddFileStore(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<FileStoreOptions>()
            .BindConfiguration(FileStoreOptions.SectionName)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Root) && Path.IsPathFullyQualified(o.Root),
                $"{FileStoreOptions.SectionName}:Root must be a full path. Set the variable {FileStoreOptions.SectionName}__Root to the folder that holds the files.")
            .ValidateOnStart();
        builder.Services.AddSingleton<FileStore>();
        builder.Services.AddHostedService<BestEffortFileStoreStartup>();
    }

    private sealed class BestEffortFileStoreStartup(FileStore fileStore) : IHostedService
    {
        private readonly ILogger _logger = Log.ForContext<BestEffortFileStoreStartup>();

        public Task StartAsync(CancellationToken cancellationToken)
        {
            using var trace = Telemetry.TraceRoot([], "file_store.prepare");
            try
            {
                var removed = fileStore.Prepare();
                if (removed > 0)
                    _logger.Here().Warning("Removed {TemporaryItemCount} temporary items of interrupted file store work", removed);
            }
            catch (Exception ex)
            {
                Telemetry.RecordError(trace.Activity, ex);
                _logger.Here().Error(ex, "Could not prepare the file store");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
