namespace Recall.Server.Auth;

public enum LoginModeState
{
    Ready,
    Unreachable,
}

public interface ILoginMode
{
    string Id { get; }
    string DisplayName { get; }
    string Kind { get; }

    Task<LoginModeState> ProbeAsync(CancellationToken cancellationToken);

    Task<string?> ProviderLogoutUrlAsync(string returnUrl, string state, CancellationToken cancellationToken);
}
