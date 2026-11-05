namespace Recall.Server.Databases.Sqlite;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Resources;

/// <summary>
/// Gives each command of EF its span. The SQLite provider makes none.
/// </summary>
internal sealed class SqlSpanInterceptor : DbCommandInterceptor
{
    private readonly ConcurrentDictionary<Guid, Activity> _running = new();

    public static SqlSpanInterceptor Instance { get; } = new();

    private SqlSpanInterceptor()
    {
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Start(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Start(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Start(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Start(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Start(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Start(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Stop(eventData.CommandId);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    {
        Stop(eventData.CommandId);
        return ValueTask.FromResult(result);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Stop(eventData.CommandId);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        Stop(eventData.CommandId);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Stop(eventData.CommandId);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        object? result, CancellationToken cancellationToken = default)
    {
        Stop(eventData.CommandId);
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => Fail(eventData);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Fail(eventData);
        return Task.CompletedTask;
    }

    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData)
        => Stop(eventData.CommandId);

    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Stop(eventData.CommandId);
        return Task.CompletedTask;
    }

    private void Start(DbCommand command, CommandEventData eventData)
    {
        var parent = Activity.Current;
        if (parent is not { IsAllDataRequested: true })
            return;

        var activity = Telemetry.Source.StartActivity("sqlite.command", ActivityKind.Client);
        Activity.Current = parent;
        if (activity is null)
            return;

        activity.SetTag("db.system.name", "sqlite");
        activity.SetTag("db.query.text", command.CommandText);
        QueryTagEnrichment.Enrich(activity, command.CommandText);
        _running[eventData.CommandId] = activity;
    }

    private void Stop(Guid commandId)
    {
        if (_running.TryRemove(commandId, out var activity))
            activity.Dispose();
    }

    private void Fail(CommandErrorEventData eventData)
    {
        if (_running.TryRemove(eventData.CommandId, out var activity))
        {
            Telemetry.MarkFailed(activity, eventData.Exception);
            activity.Dispose();
        }
    }
}
