namespace Recall.Server.Resources.Jobs;

using BackWave.Core;
using BackWave.Dashboard;
using BackWave.Generated;
using BackWave.Hosting;
using BackWave.Sqlite;
using Databases;
using Domain.Sessions.Jobs;

public static class JobHosting
{
    public const string SystemQueue = "system";

    public const string CleanupQueue = "cleanup";

    public const string HealthCheckName = "backwave";

    /// <summary>Inside the 10 s that <c>docker stop</c> waits by default. The telemetry files flush after the host stops.</summary>
    private static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(8);

    /// <summary>A job that does not stop in this time gets a lease that ends, so a later start runs it again.</summary>
    public static readonly TimeSpan JobShutdownBudget = TimeSpan.FromSeconds(6);

    /// <summary>The dashboard shows a finished job for this time. A dead-letter job stays for the BackWave default of 14 days.</summary>
    public static readonly TimeSpan KeepSucceeded = TimeSpan.FromDays(14);

    private static readonly TimeSpan StoreBusyTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] DevelopmentAndTestEnvironments = [Environments.Development, "FunctionalTesting", "IntegrationTesting"];

    public static void AddJobs(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(JobOptions.SectionName);
        var settingsAtRegistration = section.Get<JobOptions>() ?? new JobOptions();
        var inDevelopmentOrTest = DevelopmentAndTestEnvironments.Contains(builder.Environment.EnvironmentName);

        builder.Services.Configure<JobOptions>(section);
        if (!inDevelopmentOrTest)
            builder.Services.PostConfigure<JobOptions>(options => options.HealthJob = new HealthJobOptions());
        builder.Services.Configure<HostOptions>(host =>
        {
            host.ShutdownTimeout = HostShutdownTimeout;
            // The web host waits for each open request, such as a live page of the dashboard, and a worker group that
            // stops after it gets no time to give back its jobs.
            host.ServicesStopConcurrently = true;
        });
        builder.Services.AddBackWave(backwave =>
        {
            backwave.UseStore(services => new SqliteJobStore(new SqliteStoreOptions
            {
                ConnectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(DatabaseConsts.DatabaseName)!,
                AutoMigrate = true,
                TablePrefix = "backwave",
                BusyTimeout = StoreBusyTimeout,
                LoggerFactory = services.GetRequiredService<ILoggerFactory>(),
            })).UseJobs(JobFailureFilter.Applied(BackWaveJobs.Module));
            if (settingsAtRegistration.RunWorkers || !inDevelopmentOrTest)
            {
                backwave.AddWorkerGroup(new WorkerGroupOptions
                {
                    Name = "system",
                    Policy = new DispatchPolicy.Strict([SystemQueue, CleanupQueue]),
                    PollInterval = TimeSpan.FromSeconds(settingsAtRegistration.PollSeconds),
                    ShutdownBudget = JobShutdownBudget,
                    Retention = new RetentionPolicy { KeepSucceeded = KeepSucceeded },
                });
                builder.Services.AddHostedService<JobHostWatch>();
                builder.Services.AddSingleton<CleanupJobWatch>();
                builder.Services.AddHostedService(services => services.GetRequiredService<CleanupJobWatch>());
            }
        });
        builder.Services.AddBackWaveDashboardMetrics();
        builder.Services.AddSingleton<JobHostStatus>();
        builder.Services.AddSingleton<SaveJobs>();
        builder.Services.AddHealthChecks().AddCheck<JobHostHealthCheck>(HealthCheckName);
    }
}
