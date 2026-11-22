namespace Recall.Server.Domain.ModelProviders;

using System.Security.Cryptography;
using Databases;
using LoginProviders;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Resources;
using Serilog;
using Tenants;

/// <summary>
/// Looks at the key ring once at each start, after the migrations. A restore of the database with no key folder, or a key
/// folder with no write, gives a new ring with no sign of it, and the keys of the providers stop working later.
/// </summary>
public sealed class KeyRingWatch(
    IServiceScopeFactory scopeFactory,
    IDataProtectionProvider dataProtection,
    ApiKeyProtector keys,
    LoginProviderSecret secrets,
    KeyRingStatus status,
    IConfiguration configuration) : IHostedService
{
    private const string MarkerWord = "recall-key-ring";

    private readonly IDataProtector _markers = dataProtection.CreateProtector("Recall.KeyRing.Marker.v1");
    private readonly ILogger _logger = Log.ForContext<KeyRingWatch>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        status.FolderIsWritable = FolderTakesAWrite(OwnerOnlyFiles.KeysDirectory(configuration));

        await using var scope = scopeFactory.CreateSystemScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var marker = await dbContext.KeyRingMarkers.TagWithOperationCallSite("key_ring.marker").FirstOrDefaultAsync(cancellationToken);
        var protectedKeys = await dbContext.ModelProviders.AsNoTracking()
            .TagWithOperationCallSite("key_ring.providers")
            .Where(p => p.ProtectedApiKey != null)
            .Select(p => p.ProtectedApiKey)
            .ToListAsync(cancellationToken);
        var protectedSecrets = await dbContext.LoginProviders.AsNoTracking()
            .TagWithOperationCallSite("key_ring.login_providers")
            .Select(p => p.ProtectedClientSecret)
            .ToListAsync(cancellationToken);

        // The client secrets of the login providers are under the same ring as the API keys (IDP-5).
        var keysAreUnreadable = protectedKeys.Any(key => keys.Read(key) is ProtectedKeyRead.Unreadable)
            || protectedSecrets.Any(secret => secrets.Read(secret) is null);
        var markerIsUnreadable = marker is not null && !Reads(marker.ProtectedValue);
        status.RingChanged = keysAreUnreadable || (markerIsUnreadable && protectedKeys.Count + protectedSecrets.Count == 0);

        if ((marker is null || (markerIsUnreadable && !keysAreUnreadable)) && NewMarker() is { } newMarker)
        {
            if (marker is null)
                await dbContext.KeyRingMarkers.AddAsync(new KeyRingMarker { ProtectedValue = newMarker }, cancellationToken);
            else
                marker.ProtectedValue = newMarker;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (status.Warning is { } warning)
            _logger.Here().Warning("{KeyRingWarning}", warning);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A key folder that holds no ring and takes no write gives no marker. The status already says so, and the journal must still start.</summary>
    private string? NewMarker()
    {
        try
        {
            return _markers.Protect(MarkerWord);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private bool Reads(string protectedValue)
    {
        try
        {
            return _markers.Unprotect(protectedValue) == MarkerWord;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool FolderTakesAWrite(string? folder)
    {
        if (folder is null)
            return true;

        var probe = Path.Combine(folder, $".write-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception fault) when (fault is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>A changed ring needs the operator, so the probe fails as it does on each Degraded. The journal still works, so the answer stays 200.</summary>
public sealed class KeyRingHealthCheck(KeyRingStatus status) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(status.Warning is { } warning ? HealthCheckResult.Degraded(warning) : HealthCheckResult.Healthy());
}
