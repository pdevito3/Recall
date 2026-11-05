namespace Recall.Server.Databases;

using System.Diagnostics;
using Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Resources;

public static class DatabaseConflict
{
    private const int UniqueViolation = 2067;
    private const int PrimaryKeyViolation = 1555;
    private const int ForeignKeyViolation = 787;
    private const int ConstraintFailed = 19;
    private const string UniqueMessage = "UNIQUE constraint failed: ";

    /// <summary>Null when the failure is a server fault. The model gives the <see cref="ConstraintErrors"/>.</summary>
    public static Exception? From(DbUpdateException exception, IModel model)
    {
        if (exception is DbUpdateConcurrencyException)
            return new StaleVersionException(exception);

        if (exception.InnerException is SqliteException { SqliteErrorCode: ConstraintFailed } failed
            && failed.Message.Contains(JournalTriggers.OwnerDeletedMessage, StringComparison.Ordinal))
            return new ForbiddenAccessException(JournalTriggers.OwnerDeletedMessage);

        if (LostToAHardDelete(exception))
            return new StaleVersionException(exception);

        if (exception.InnerException is not SqliteException
            { SqliteExtendedErrorCode: UniqueViolation or PrimaryKeyViolation } sqlite)
            return null;

        var constraint = ConstraintNameOf(sqlite.Message, model);
        return ConstraintErrors.Of(model).GetValueOrDefault(constraint) switch
        {
            null => new ConstraintConflictException(constraint, string.Empty, ConstraintConflictException.DuplicateMessage, exception),
            ConstraintError.StaleVersion => new StaleVersionException(exception),
            ConstraintError.RequestField error => new ConstraintConflictException(constraint, error.Field, error.Message, exception),
            var error => throw new UnreachableException(
                $"The constraint {constraint} has a {error.GetType().FullName}, which {nameof(DatabaseConflict)} does not map to a response."),
        };
    }

    // A journal row is hard-deleted, and a Revision inserts before the version check of its Session. So a save that lost
    // to a delete fails on the key of its parent, and the next run finds no row.
    private static bool LostToAHardDelete(DbUpdateException exception)
        => exception.InnerException is SqliteException { SqliteExtendedErrorCode: ForeignKeyViolation }
            && exception.Entries.Any(entry => entry.Metadata.FindDeclaredQueryFilter(QueryFilterNames.JournalOwner) is not null);

    /// <summary>
    /// SQLite names the columns of the violated index, as <c>UNIQUE constraint failed: users.tenant_id, users.email</c>,
    /// and not the index.
    /// </summary>
    internal static string ConstraintNameOf(string message, IModel model)
    {
        var start = message.IndexOf(UniqueMessage, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        var named = message[(start + UniqueMessage.Length)..].Trim().TrimEnd('\'', '.');
        var failed = named.Split(", ");
        var table = failed[0].Split('.')[0];
        var columns = failed.Select(column => column[(column.IndexOf('.') + 1)..]).ToHashSet();
        var store = StoreObjectIdentifier.Table(table);

        bool SameColumns(IEnumerable<IReadOnlyProperty> properties)
            => properties.Select(p => p.GetColumnName(store)).ToHashSet().SetEquals(columns);

        foreach (var entityType in model.GetEntityTypes().Where(e => e.GetTableName() == table))
        {
            if (entityType.GetIndexes().FirstOrDefault(i => i.IsUnique && SameColumns(i.Properties)) is { } index)
                return index.GetDatabaseName() ?? string.Empty;

            if (entityType.GetKeys().FirstOrDefault(k => SameColumns(k.Properties)) is { } key)
                return key.GetName() ?? string.Empty;
        }

        return named;
    }
}
