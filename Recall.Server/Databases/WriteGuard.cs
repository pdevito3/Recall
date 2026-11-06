namespace Recall.Server.Databases;

using System.Data.Common;
using Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
/// A command on a connection that EF does not run does not come here, so the build bans those APIs in the Server.
/// </summary>
internal sealed class WriteGuard : DbCommandInterceptor, IDbTransactionInterceptor
{
    private const string HandlerRefusal =
        "A domain event handler must not run SQL other than a LINQ query, such as ExecuteUpdateAsync, " +
        "ExecuteDeleteAsync, FromSql, SqlQuery, or ExecuteSql. Read with LINQ, and change the entities " +
        "through the dbContext of Handle, so that the save writes them.";

    private const string TransactionRefusal =
        "A domain event handler must not begin or use a transaction. " +
        "The save that runs the handler commits its changes in one transaction.";

    public static WriteGuard Instance { get; } = new();

    private WriteGuard()
    {
    }

    internal static string? Refusal(
        CommandSource source, string commandText, bool runningDomainEventHandlers, IReadOnlySet<string> guardedTables)
    {
        if (runningDomainEventHandlers)
            return source == CommandSource.LinqQuery ? null : HandlerRefusal;

        if (source is CommandSource.LinqQuery or CommandSource.SaveChanges or CommandSource.Migrations
            or CommandSource.ValueGenerator or CommandSource.Scaffolding)
            return null;

        var canWrite = source != CommandSource.FromSqlQuery || SqlText.IsWrite(commandText);
        return canWrite && SqlText.FirstNamed(commandText, guardedTables) is { } table
            ? $"{table} is audited, so write it through SaveChangesAsync. ExecuteUpdate, ExecuteDelete, " +
              "and raw SQL do not write audit entries. An entity type that needs no audit has [NotAudited(reason)]."
            : null;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        RefuseCommand(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        RefuseCommand(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        RefuseCommand(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        RefuseCommand(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        RefuseCommand(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        RefuseCommand(command, eventData);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        RefuseTransaction(eventData.Context);
        return result;
    }

    public ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        RefuseTransaction(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public DbTransaction TransactionUsed(
        DbConnection connection, TransactionEventData eventData, DbTransaction result)
    {
        RefuseTransaction(eventData.Context);
        return result;
    }

    public ValueTask<DbTransaction> TransactionUsedAsync(
        DbConnection connection, TransactionEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        RefuseTransaction(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void RefuseCommand(DbCommand command, CommandEventData eventData)
    {
        if (eventData.Context is AppDbContext context
            && Refusal(eventData.CommandSource, command.CommandText, context.IsRunningDomainEventHandlers,
                AuditModel.Of(context.Model).GuardedTables) is { } refusal)
            throw new InvalidOperationException(refusal);
    }

    private static void RefuseTransaction(DbContext? context)
    {
        if (context is AppDbContext { IsRunningDomainEventHandlers: true })
            throw new InvalidOperationException(TransactionRefusal);
    }
}
