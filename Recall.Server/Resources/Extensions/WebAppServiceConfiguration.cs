namespace Recall.Server.Resources.Extensions;

using Auth;
using Databases;
using Databases.Sqlite;
using Domain.Tenants;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Storage;
using ZiggyCreatures.Caching.Fusion;

public static class WebAppServiceConfiguration
{
    /// <summary>
    /// Adds the core services: the database, the domain event handlers, MediatR, the caches, and the tenant scope. It adds no transport.
    /// </summary>
    public static void ConfigureServices(this WebApplicationBuilder builder)
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
        builder.Services.AddAuthCore(builder.Configuration);
        builder.AddFileStore();
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
                .AddInterceptors(
                    SqliteConnectionSetup.Instance,
                    UtcTextParameters.Instance,
                    SqlSpanInterceptor.Instance,
                    serviceProvider.GetRequiredService<TenantLookupInterceptor>());
        });
        builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

        builder.Services.AddDomainEvents(typeof(Program).Assembly);
    }
}
