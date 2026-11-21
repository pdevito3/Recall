namespace Recall.Server.Exceptions;

/// <summary>Each slot of a costly step, such as a password hash, stayed taken past its wait, so nothing changed (PWD-11).</summary>
public sealed class ServerBusyException(string message) : Exception(message);
