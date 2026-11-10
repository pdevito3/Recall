namespace Recall.Server.Exceptions;

/// <summary>
/// A write did not get the write lock of the database within its limit, so nothing changed. The writer line throws it,
/// and <see cref="DomainError"/> reads SQLite error 5 (busy) in the same way.
/// </summary>
public sealed class DatabaseBusyException(Exception? innerException = null) : Exception(BusyMessage, innerException)
{
    public const string BusyMessage =
        "The database of the server did not answer in time, so nothing changed. Try again in a minute. If this keeps happening, ask the Owner to check the disk of the server.";
}
