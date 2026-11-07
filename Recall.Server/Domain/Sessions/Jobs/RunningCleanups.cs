namespace Recall.Server.Domain.Sessions.Jobs;

using System.Collections.Concurrent;

/// <summary>
/// The call that a person can stop. One process runs the jobs and serves the page, so memory is enough. After a restart no
/// call runs, and the stored state makes the next claim skip the run.
/// </summary>
public sealed class RunningCleanups
{
    private readonly ConcurrentDictionary<string, Run> _runs = new();

    /// <summary>A job calls this before it reads the state, so a stop between the read and the call still reaches the call.</summary>
    public Run Watch(string runId, CancellationToken hostStop)
    {
        var run = new Run(this, runId, hostStop);
        _runs[runId] = run;
        return run;
    }

    public void Stop(string runId)
    {
        if (_runs.TryGetValue(runId, out var run))
            run.Cancel();
    }

    public sealed class Run : IDisposable
    {
        private readonly RunningCleanups _owner;
        private readonly string _runId;
        private readonly CancellationTokenSource _call;

        internal Run(RunningCleanups owner, string runId, CancellationToken hostStop)
        {
            _owner = owner;
            _runId = runId;
            _call = CancellationTokenSource.CreateLinkedTokenSource(hostStop);
        }

        public CancellationToken Token => _call.Token;

        public void EndAfter(TimeSpan limit) => _call.CancelAfter(limit);

        internal void Cancel()
        {
            try
            {
                _call.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The job ended in the same moment, so no call remains to stop.
            }
        }

        public void Dispose()
        {
            _owner._runs.TryRemove(new KeyValuePair<string, Run>(_runId, this));
            _call.Dispose();
        }
    }
}
