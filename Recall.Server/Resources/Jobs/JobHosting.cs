namespace Recall.Server.Resources.Jobs;

using BackWave.Core;
using BackWave.Dashboard;
using BackWave.Generated;
using BackWave.Hosting;
using BackWave.Pro;
using BackWave.Sqlite;
using Databases;
using Domain.Recordings.Jobs;
using Domain.Sessions.Jobs;
using Domain.Summaries.Jobs;

public static class JobHosting
{
    public const string SystemQueue = "system";

    public const string TranscriptionQueue = "transcription";

    public const string HealthCheckName = "backwave";

    /// <summary>Inside the 10 s that <c>docker stop</c> waits by default. The telemetry files flush after the host stops.</summary>
    private static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(8);

    /// <summary>A job that does not stop in this time gets a lease that ends, so a later start runs it again.</summary>
    public static readonly TimeSpan JobShutdownBudget = TimeSpan.FromSeconds(6);

    /// <summary>The dashboard shows a finished job for this time. A dead-letter job stays for the BackWave default of 14 days.</summary>
    public static readonly TimeSpan KeepSucceeded = TimeSpan.FromDays(14);

    private static readonly TimeSpan StoreBusyTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] DevelopmentAndTestEnvironments = [Environments.Development, "FunctionalTesting", "IntegrationTesting"];

    private const int TranscriptionsThatFitInMemory = 1;

    public static WorkerGroupOptions SystemWorkers(JobOptions settings) => new()
    {
        Name = "system",
        Policy = new DispatchPolicy.Strict([SystemQueue]),
        PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
        ShutdownBudget = JobShutdownBudget,
        Retention = new RetentionPolicy { KeepSucceeded = KeepSucceeded },
    };

    public static WorkerGroupOptions TranscriptionWorkers(JobOptions settings) => new()
    {
        Name = TranscriptionQueue,
        PoolSize = TranscriptionsThatFitInMemory,
        Policy = new DispatchPolicy.Strict([TranscriptionQueue]),
        PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
        ShutdownBudget = JobShutdownBudget,
        Retention = new RetentionPolicy { KeepSucceeded = KeepSucceeded },
    };

    /// <summary>One worker for each queue, and a queue runs one job at a time, so a tenant holds one worker at most.</summary>
    public static WorkerGroupOptions SummaryWorkers(JobOptions settings) => new()
    {
        Name = "summaries",
        PoolSize = SummaryQueues.Count * SummaryQueues.JobsAtOnceInAQueue,
        Policy = SummaryQueues.Policy,
        PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
        ShutdownBudget = JobShutdownBudget,
        Retention = new RetentionPolicy { KeepSucceeded = KeepSucceeded },
    };

    /// <summary>A queue limit is what a tenant can hold, and the pool has room for each queue at its limit.</summary>
    public static WorkerGroupOptions CleanupWorkers(JobOptions settings) => new()
    {
        Name = "cleanup",
        PoolSize = CleanupQueues.Workers,
        Policy = CleanupQueues.Policy,
        PollInterval = TimeSpan.FromSeconds(settings.PollSeconds),
        ShutdownBudget = JobShutdownBudget,
        Retention = new RetentionPolicy { KeepSucceeded = KeepSucceeded },
    };

    // The composition root gives the entrypoints that BackWave registers, such as its MCP server.
    public static void AddJobs(this WebApplicationBuilder builder, Action<BackWaveBuilder>? entrypoints = null)
    {
        var section = builder.Configuration.GetSection(JobOptions.SectionName);
        var settingsAtRegistration = section.Get<JobOptions>() ?? new JobOptions();
        var inDevelopmentOrTest = DevelopmentAndTestEnvironments.Contains(builder.Environment.EnvironmentName);

        builder.Services.Configure<JobOptions>(section);
        if (!inDevelopmentOrTest)
            builder.Services.PostConfigure<JobOptions>(options =>
            {
                options.HealthJob = new HealthJobOptions();
                options.KillBeforeTenantFolderDelete = false;
            });
        builder.Services.Configure<HostOptions>(host =>
        {
            host.ShutdownTimeout = HostShutdownTimeout;
            // The web host waits for each open request, such as a live page of the dashboard, and a worker group that
            // stops after it gets no time to give back its jobs.
            host.ServicesStopConcurrently = true;
        });
        // A hosted service starts in the order of its registration, so each limit is in place before a worker group can claim.
        if (settingsAtRegistration.RunWorkers || !inDevelopmentOrTest)
            builder.Services.AddHostedService<QueueLimits>();
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
            entrypoints?.Invoke(backwave);
            if (settingsAtRegistration.RunWorkers || !inDevelopmentOrTest)
            {
                backwave.AddWorkerGroup(SystemWorkers(settingsAtRegistration));
                backwave.AddWorkerGroup(CleanupWorkers(settingsAtRegistration));
                backwave.AddWorkerGroup(TranscriptionWorkers(settingsAtRegistration));
                backwave.AddWorkerGroup(SummaryWorkers(settingsAtRegistration));
                builder.Services.AddHostedService<JobHostWatch>();
                builder.Services.AddSingleton<CleanupJobWatch>();
                builder.Services.AddHostedService(services => services.GetRequiredService<CleanupJobWatch>());
                builder.Services.AddHostedService<RecordingSweepService>();
                builder.Services.AddSingleton<SummaryJobWatch>();
                builder.Services.AddHostedService(services => services.GetRequiredService<SummaryJobWatch>());
                builder.Services.AddHostedService<SummaryScheduleSync>();
            }
        });
        // A keyless BackWave Pro logs one warning at the start and changes no feature.
        builder.Services.AddBackWavePro();
        builder.Services.AddBackWaveDashboardMetrics();
        builder.Services.AddSingleton<JobHostStatus>();
        builder.Services.AddSingleton<SaveJobs>();
        builder.Services.AddSingleton<RunningCleanups>();
        builder.Services.AddSingleton<SummarySchedules>();
        builder.Services.AddHealthChecks().AddCheck<JobHostHealthCheck>(HealthCheckName);
    }
}
