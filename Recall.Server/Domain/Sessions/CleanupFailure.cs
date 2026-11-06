namespace Recall.Server.Domain.Sessions;

/// <summary>Why a Cleanup failed. It is a closed list, because the words of a provider can hold the text of a request.</summary>
public enum CleanupFailure
{
    None = 0,
    NoModelProvider = 1,
    KeyUnreadable = 2,
    ProviderRefused = 3,
    ProviderUnavailable = 4,
    BadModelOutput = 5,
    Interrupted = 6,
}
