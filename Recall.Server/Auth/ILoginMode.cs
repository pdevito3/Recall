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

    /// <summary>The page where a person ends the session at the provider, if the provider has one. A sign-out of Recall does not end that session.</summary>
    Task<string?> ProviderLogoutUrlAsync(CancellationToken cancellationToken);
}
