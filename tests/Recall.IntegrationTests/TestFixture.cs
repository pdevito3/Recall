namespace Recall.IntegrationTests;

using AutoBogus;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Extensions;
using Recall.SharedTestHelpers;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Recall.Server.Databases.Sqlite;
using NSubstitute;

public sealed class TestFixture : IAsyncDisposable
{
    /// <summary>
    /// The one machine client that the test configuration lets see and manage all tenants.
    /// </summary>
    public const string TrustedMachineClientId = "trusted-machine";

    /// <summary>
    /// The one machine client that the test configuration lets see all tenants, but not manage them.
    /// </summary>
    public const string ReadOnlyMachineClientId = "read-only-machine";

    public const string FirstPartyClientId = "test-app";

    /// <summary>The issuer of the one entry of <c>Auth:Providers</c> in these tests.</summary>
    public const string LinkIssuer = "https://provider.test";

    public static IServiceScopeFactory BaseScopeFactory { get; private set; } = null!;
    private static string _connectionString = null!;
    public static TestSeed Seed { get; private set; } = null!;
    public static Tenant DefaultTenant => Seed.Tenant;
    public static User DefaultUser => Seed.User;
    private readonly SqliteTestDatabase _database = new();
    private static readonly TemporaryFileRoot FileRoot = new();

    public async Task InitializeAsync()
    {
        ConfigureAutoFaker();

        // Tests never reload configuration. A file watcher on appsettings can hang on macOS when the system is busy.
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");

        _connectionString = _database.ConnectionString;

        var provider = AddServices().BuildServiceProvider();
        BaseScopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // Run migrations and seed in a system scope. With no opt-in, a scope with no HTTP request sees no tenant.
        await using var scope = BaseScopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Database.MigrateAsync();

        // Create the seed once for all tests. The seed user is the current user,
        // so ITenantIdProvider resolves the seed tenant.
        Seed = TestSeed.Generate();
        dbContext.Tenants.Add(Seed.Tenant);
        dbContext.Users.Add(Seed.User);
        await dbContext.SaveChangesAsync();

        // Set TestContext so the DTO fakers use the seed tenant by default
        TestContext.DefaultTenantId = Seed.Tenant.Id;
    }

    /// <summary>
    /// Runs SQL on a connection of its own, outside AppDbContext, so the write guard does not see it and no audit
    /// entry records it. Use it to make a state that the domain cannot make, or to play the write of another process.
    /// </summary>
    public static async Task ExecuteOutOfBandAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(UnpooledConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value is DateTimeOffset date ? UtcTextConverter.ToText(date) : value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The connection string of the test database, with no connection pool.</summary>
    public static string UnpooledConnectionString
        => new SqliteConnectionStringBuilder(_connectionString) { Pooling = false }.ConnectionString;

    public static ServiceProvider BuildServiceProvider(Action<IServiceCollection> addTestServices, string? connectionString = null)
    {
        var services = AddServices(connectionString);
        addTestServices(services);
        return services.BuildServiceProvider();
    }

    private static IServiceCollection AddServices(string? connectionString = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = TestingConsts.IntegrationTestingEnvName
        });
        builder.Configuration[$"ConnectionStrings:{DatabaseConsts.DatabaseName}"] = connectionString ?? _connectionString;
        builder.Configuration["FileStore:Root"] = FileRoot.Path;

        builder.Configuration["Auth:Authority"] = "https://localhost";
        builder.Configuration["Auth:Audience"] = "test-api";
        builder.Configuration["Auth:ClientId"] = FirstPartyClientId;
        // The provider of the links that the tests add, so that the last-Admin rule counts them (IDP-14).
        builder.Configuration["Auth:Providers:0:Id"] = "provider";
        builder.Configuration["Auth:Providers:0:Issuer"] = LinkIssuer;
        builder.Configuration["Auth:Providers:0:ClientId"] = "recall-test-client";
        builder.Configuration["Auth:Providers:0:ClientSecret"] = "recall-test-secret";
        builder.Configuration[$"{TenancyOptions.SectionName}:{nameof(TenancyOptions.ManageAllTenantsClientIds)}:0"] = TrustedMachineClientId;
        builder.Configuration[$"{TenancyOptions.SectionName}:{nameof(TenancyOptions.ReadAllTenantsClientIds)}:0"] = ReadOnlyMachineClientId;

        builder.ConfigureServices();

        builder.Services.ReplaceServiceWithScopedMock<ICurrentUserService>();

        return builder.Services;
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        FileRoot.Dispose();
    }

    private static void ConfigureAutoFaker()
    {
        AutoFaker.Configure(builder =>
        {
            builder.WithDateTimeKind(DateTimeKind.Utc)
                .WithRecursiveDepth(3)
                .WithTreeDepth(1)
                .WithRepeatCount(1);
        });
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection ReplaceServiceWithScopedMock<TService>(this IServiceCollection services)
        where TService : class
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor != null)
        {
            services.Remove(descriptor);
        }

        services.AddScoped(_ => Substitute.For<TService>());
        return services;
    }
}
