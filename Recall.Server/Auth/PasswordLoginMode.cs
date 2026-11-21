namespace Recall.Server.Auth;

/// <summary>The login with a login name and a password (PWD-1). The Server runs it, so it is always ready and has no logout step at a provider.</summary>
public sealed class PasswordLoginMode : ILoginMode
{
    public const string ModeId = "password";

    public static PasswordLoginMode Instance { get; } = new();

    private PasswordLoginMode()
    {
    }

    public string Id => ModeId;
    public string DisplayName => "Password";
    public string Kind => "password";

    public Task<LoginModeState> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(LoginModeState.Ready);

    public Task<string?> ProviderLogoutUrlAsync(string returnUrl, string state, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
