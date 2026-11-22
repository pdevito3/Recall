namespace Recall.Server.Exceptions;

/// <summary>
/// Each slot of a costly step, such as a password hash, stayed taken past its wait, so nothing changed (PWD-11). The 429
/// tells the caller to try again after <see cref="RetryAfter"/>.
/// </summary>
public sealed class ServerBusyException(string message, TimeSpan retryAfter) : Exception(message)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
