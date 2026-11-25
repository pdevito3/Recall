namespace Recall.Server.Exceptions;

/// <summary>
/// The caller based the change on a version of the record that is no longer current.
/// </summary>
public sealed class StaleVersionException : Exception
{
    public const string Field = "version";
    public const string StaleMessage = "This record changed after you loaded it. Reload it, then make your change again.";

    public StaleVersionException(Exception? innerException = null)
        : base(StaleMessage, innerException)
    {
    }

    /// <param name="message">The words of a record whose own rule names what changed, such as the accept of a batch.</param>
    /// <param name="innerException">The failure that caused it, if any.</param>
    public StaleVersionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
