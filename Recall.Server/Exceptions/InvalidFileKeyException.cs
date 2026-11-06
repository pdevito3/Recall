namespace Recall.Server.Exceptions;

/// <summary>
/// Exception thrown when a value cannot be a file key, for example a path that leaves the root of the file store.
/// </summary>
public sealed class InvalidFileKeyException(string message) : ValidationException("fileKey", message);
