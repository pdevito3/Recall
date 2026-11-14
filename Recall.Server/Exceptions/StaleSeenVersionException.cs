namespace Recall.Server.Exceptions;

/// <summary>
/// The version of the record that the caller saw is no longer current, in a command that runs again when raced. It gives
/// the 409 of a stale version. It is not a <see cref="StaleVersionException"/>, so <c>RaceRetryBehavior</c> does not run
/// the command again on it: a second run reads the same moved version.
/// </summary>
public sealed class StaleSeenVersionException() : Exception(StaleVersionException.StaleMessage);
