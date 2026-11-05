namespace Recall.IntegrationTests.Auth;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Auth;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.SharedTestHelpers.Utilities;

/// <summary>
/// A database of its own, because the first-owner rule reads whether any tenant exists, and the shared database has one.
/// </summary>
internal sealed class AuthTestDatabase : IAsyncDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly List<ServiceProvider> _providers = [];

    public string ClaimFilePath => Path.Combine(
        Path.GetDirectoryName(new SqliteConnectionStringBuilder(_database.ConnectionString).DataSource)!, OwnerClaim.FileName);

    public static async Task<AuthTestDatabase> CreateAsync()
    {
        var database = new AuthTestDatabase();
        await using var scope = database.BuildServices().GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        return database;
    }

    public ServiceProvider BuildServices(bool openSignup = false, IInterceptor? interceptor = null, Action<IServiceCollection>? configure = null)
    {
        var connectionString = new SqliteConnectionStringBuilder(_database.ConnectionString) { Pooling = false }.ConnectionString;
        var provider = TestFixture.BuildServiceProvider(services =>
        {
            services.PostConfigure<AuthOptions>(options => options.OpenSignup = openSignup);
            if (interceptor is not null)
                services.ConfigureDbContext<AppDbContext>((_, options) => options.AddInterceptors(interceptor));
            configure?.Invoke(services);
        }, connectionString);
        _providers.Add(provider);
        return provider;
    }

    public async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        await using var scope = _providers[0].GetRequiredService<IServiceScopeFactory>().CreateSystemScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
        await _database.DisposeAsync();
    }
}
