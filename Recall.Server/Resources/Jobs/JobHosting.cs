namespace Recall.Server.Resources.Jobs;

using BackWave.Core;
using BackWave.Generated;
using BackWave.Hosting;
using BackWave.Sqlite;
using Databases;

public static class JobHosting
{
    public const string SystemQueue = "system";

    /// <summary>The time that <c>docker stop</c> waits before it kills the process. The compose file sets no other value.</summary>
    public static readonly TimeSpan DockerStopGrace = TimeSpan.FromSeconds(10);

    /// <summary>The host must stop inside <see cref="DockerStopGrace"/>, and it flushes the telemetry files after it stops.</summary>
    public static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(8);

    /// <summary>A job that does not stop in this time gets a lease that ends, so a later start runs it again.</summary>
    public static readonly TimeSpan JobShutdownBudget = TimeSpan.FromSeconds(6);

    /// <summary>The dashboard shows a finished job for this time. A dead-letter job stays for the BackWave default of 14 days.</summary>
    public static readonly TimeSpan KeepSucceeded = TimeSpan.FromDays(14);

    private static readonly TimeSpan StoreBusyTimeout = TimeSpan.FromSeconds(30);

    public static void AddJobs(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(JobOptions.SectionName);
        var pollSeconds = section.Get<JobOptions>()?.PollSeconds ?? new JobOptions().PollSeconds;

        builder.Services.Configure<JobOptions>(section);
        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = HostShutdownTimeout);
        builder.Services.AddAntiforgery();
        builder.Services.AddBackWave(backwave =>
        {
            backwave.UseStore(services => new SqliteJobStore(new SqliteStoreOptions
            {
                ConnectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(DatabaseConsts.DatabaseName)!,
                AutoMigrate = true,
                TablePrefix = "backwave",
                BusyTimeout = StoreBusyTimeout,
                LoggerFactory = services.GetRequiredService<ILoggerFactory>(),
            })).UseJobs(BackWaveJobs.Module);
            backwave.AddWorkerGroup(new WorkerGroupOptions
            {
                Name = "system",
                Policy = new DispatchPolicy.Strict([SystemQueue]),
                PollInterval = TimeSpan.FromSeconds(pollSeconds),
                ShutdownBudget = JobShutdownBudget,
                Retention = new RetentionPolicy { KeepSucceeded = KeepSucceeded },
            });
        });
        builder.Services.AddHostedService<HealthJobOnStart>();
    }
}
