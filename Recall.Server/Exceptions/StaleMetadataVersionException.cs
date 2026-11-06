namespace Recall.Server.Exceptions;

/// <summary>
/// The caller based a metadata save on a version of the metadata that is no longer current. It is not a
/// <see cref="StaleVersionException"/>, which <see cref="Resources.RaceRetryBehavior{TRequest,TResponse}"/> reads as a lost
/// race on the row: a run again cannot make this save current, and the caller must get this refusal on the first run.
/// </summary>
public sealed class StaleMetadataVersionException() : Exception(StaleVersionException.StaleMessage)
{
    public const string Field = "metadataVersion";
}
