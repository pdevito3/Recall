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
}
