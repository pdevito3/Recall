namespace Recall.Server.Auth;

using System.Security.Cryptography;
using System.Text;
using Databases;
using Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Resources;
using Serilog;

public enum SetupCodeCheck
{
    Wrong,
    Right,

    /// <summary>No code is live, because a setup used it or the Server booted with a tenant.</summary>
    Void,
}

/// <summary>
/// The one-use code of the first setup (PWD-2). With a password, nothing decides who reaches a fresh install, so the
/// setup needs a secret that only a reader of the Server log has. It lives in memory, and a restart makes a new one.
/// </summary>
public sealed class SetupCode
{
    public const int Length = 20;

    private const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private readonly Lock _gate = new();
    private string? _code;

    public bool IsLive
    {
        get
        {
            lock (_gate)
                return _code is not null;
        }
    }

    /// <summary>The live code, for a test that plays the operator who reads the log.</summary>
    internal string? Current
    {
        get
        {
            lock (_gate)
                return _code;
        }
    }

    /// <summary>Makes the code. It returns the code once, so that the caller writes it to the log once.</summary>
    public string Make()
    {
        lock (_gate)
            return _code = RandomNumberGenerator.GetString(Base32, Length);
    }

    /// <summary>The typed code in any case, with spaces at its ends, compared in a fixed time.</summary>
    public SetupCodeCheck Check(string? typed)
    {
        string? code;
        lock (_gate)
            code = _code;
        if (code is null)
            return SetupCodeCheck.Void;

        var given = Encoding.UTF8.GetBytes((typed ?? "").Trim().ToUpperInvariant());
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(code)) ? SetupCodeCheck.Right : SetupCodeCheck.Wrong;
    }

    public void Void()
    {
        lock (_gate)
            _code = null;
    }

    public override string ToString() => "[setup code]";
}

/// <summary>
/// Makes the setup code at boot while the install has no OIDC provider and no live tenant. With a provider, the first OIDC
/// login makes the Owner (PWD-4), so no code is made. It runs after the migrations, because each hosted service starts in
/// the order of its registration and the migrations register first.
/// </summary>
public sealed class SetupCodeAtBoot(IServiceScopeFactory scopeFactory, SetupCode setupCode, IOptions<AuthOptions> options) : IHostedService
{
    private readonly ILogger _logger = Log.ForContext<SetupCodeAtBoot>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Providers.Count > 0 || await AnyLiveTenantAsync(scopeFactory, cancellationToken))
            return;

        // The one line that holds the code. The operator reads it with docker compose logs, or in /data/logs.
        _logger.Here().Warning(
            "This Recall server is not set up. Open /auth/password/setup in a browser and give the setup code {SetupCode:l}. A restart makes a new code",
            setupCode.Make());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static async Task<bool> AnyLiveTenantAsync(IServiceScopeFactory scopeFactory, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateSystemScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Tenants
            .TagWithOperationCallSite("auth.setup.any_live_tenant")
            .AnyAsync(cancellationToken);
    }
}
