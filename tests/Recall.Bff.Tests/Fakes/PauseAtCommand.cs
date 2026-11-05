namespace Recall.Bff.Tests.Fakes;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

public sealed class PauseAtCommand(string sql, int fromCommand = 1) : DbCommandInterceptor
{
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _seen;
    private volatile bool _armed;

    public Task Arrived => _arrived.Task;

    public void Arm() => _armed = true;

    public void Release() => _released.TrySetResult();

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await PauseIfMatchedAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await PauseIfMatchedAsync(command);
        return result;
    }

    private async Task PauseIfMatchedAsync(DbCommand command)
    {
        if (!_armed || !command.CommandText.Contains(sql, StringComparison.Ordinal)
            || Interlocked.Increment(ref _seen) < fromCommand)
            return;

        _arrived.TrySetResult();
        await _released.Task;
    }
}
