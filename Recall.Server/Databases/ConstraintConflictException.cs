namespace Recall.Server.Databases;

using Exceptions;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A save that a unique index stopped. <see cref="AppDbContext"/> throws it in place of the
/// <see cref="DbUpdateException"/>.
/// </summary>
public sealed class ConstraintConflictException(
    string constraint,
    string property,
    string message,
    Exception innerException)
    : ConflictException(property, message, innerException)
{
    public const string DuplicateMessage = "A record with this value already exists.";

    public string Constraint { get; } = constraint;
}
