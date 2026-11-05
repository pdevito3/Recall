namespace Recall.Server.Auth;

public enum LoginModeState
{
    Ready,
    Unreachable,
}

/// <summary>A way to sign in. It has only what the status answer and the chooser page need.</summary>
public interface ILoginMode
{
    string Id { get; }
    string DisplayName { get; }
    string Kind { get; }

    /// <summary>Cached for 2 seconds, one call at a time, with a limit of 3 seconds.</summary>
    Task<LoginModeState> ProbeAsync(CancellationToken cancellationToken);
}
