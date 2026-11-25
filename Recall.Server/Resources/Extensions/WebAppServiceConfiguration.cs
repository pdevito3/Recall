namespace Recall.Server.Resources.Extensions;

using Auth;
using Databases;
using Databases.Sqlite;
using Domain.ModelProviders.Clients;
using Domain.Recordings.Transcription;
using Domain.Tenants;
using Domain.Users;
using Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Storage;
using ZiggyCreatures.Caching.Fusion;

public static class WebAppServiceConfiguration
{
    /// <summary>
    /// Adds the core services: the database, the domain event handlers, MediatR, the caches, and the tenant scope. It adds no transport.
    /// </summary>
    // The composition root gives the BackWave entrypoints, because the core does not see an entrypoint.
    public static void ConfigureServices(this WebApplicationBuilder builder, Action<BackWave.Hosting.BackWaveBuilder>? jobEntrypoints = null)
    {
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddFusionCache()
            .WithDefaultEntryOptions(options =>
            {
                options.Duration = TimeSpan.FromMinutes(5);
                options.IsFailSafeEnabled = true;
                options.FailSafeMaxDuration = TimeSpan.FromHours(2);
                options.FailSafeThrottleDuration = TimeSpan.FromSeconds(30);
            });
        builder.Services.Configure<TenancyOptions>(builder.Configuration.GetSection(TenancyOptions.SectionName));
        builder.Services.AddSingleton(Options.Create(FirstPartyClientOptions.FromConfiguration(builder.Configuration)));
        builder.Services.AddApplicationServices();
        builder.Services.AddScoped<SystemTenantAccess>();
        builder.Services.AddScoped<Domain.Sessions.Cleanup.CleanupCompletion>();
        builder.Services.AddScoped<Domain.Sessions.Cleanup.CleanupWatch>();
        builder.Services.AddModelProviders();
        builder.Services.AddAuthCore(builder.Configuration);
        builder.AddFileStore();
        builder.AddTranscription();
        builder.AddJobs(jobEntrypoints);
        builder.Services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
            // Outermost behavior, so its span covers every later behavior and the handler itself.
            cfg.AddOpenBehavior(typeof(RequestTracingBehavior<,>));
            cfg.AddOpenBehavior(typeof(CallerIdentityBehavior<,>));
            cfg.AddOpenBehavior(typeof(RequestValidationBehavior<,>));
            // Only for an IRunsAgainWhenRaced request. Each run again runs AuditRequestBehavior and the handler again.
            cfg.AddOpenBehavior(typeof(RaceRetryBehavior<,>));
            // Innermost behavior, next to the handler whose saves it names.
            cfg.AddOpenBehavior(typeof(AuditRequestBehavior<,>));
        });

        builder.Services.AddSingleton<TenantLookupInterceptor>();
        builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            var connectionString = builder.Configuration.GetConnectionString(DatabaseConsts.DatabaseName);

            options.UseSqlite(connectionString)
                .UseSnakeCaseNamingConvention()
                .ConfigureWarnings(LeaveFailureLogsToTheCaller)
                .AddInterceptors(
                    SqliteConnectionSetup.Instance,
                    UtcTextParameters.Instance,
                    SqlSpanInterceptor.Instance,
                    serviceProvider.GetRequiredService<TenantLookupInterceptor>());
        });
        builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
        builder.Services.AddSingleton<WalScrub>();
        builder.Services.AddHostedService(services => services.GetRequiredService<WalScrub>());

        builder.Services.AddDomainEvents(typeof(Program).Assembly);
    }

    private static void LeaveFailureLogsToTheCaller(WarningsConfigurationBuilder warnings) => warnings.Log(
        (RelationalEventId.CommandError, LogLevel.Debug),
        (CoreEventId.SaveChangesFailed, LogLevel.Debug));
}
