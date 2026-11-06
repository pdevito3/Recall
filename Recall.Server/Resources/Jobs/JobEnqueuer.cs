namespace Recall.Server.Resources.Jobs;

using System.Data.Common;
using BackWave;
using BackWave.Storage;

/// <summary>Enqueues in the transaction of one save. <see cref="Databases.AppDbContext.SaveAndEnqueueAsync"/> makes it.</summary>
public sealed class JobEnqueuer
{
    private readonly BackWaveClient _client;
    private readonly DbTransaction _transaction;

    internal JobEnqueuer(BackWaveClient client, DbTransaction transaction)
    {
        _client = client;
        _transaction = transaction;
    }

    public async Task<Guid> EnqueueAsync<TJob>(TJob job, DateTimeOffset dueTime, CancellationToken cancellationToken = default)
        where TJob : notnull
        => await _client.EnqueueAsync(job, dueTime, transaction: _transaction, cancellationToken: cancellationToken);
}

/// <summary>
/// The first use of the store migrates the job tables on a connection of its own, and that needs the write lock. So a
/// save makes the store ready before its transaction, and it cannot wait on its own lock.
/// </summary>
public sealed class SaveJobs(BackWaveClient client, IJobStore store)
{
    private volatile bool _storeIsReady;

    public async Task MakeStoreReadyAsync(CancellationToken cancellationToken)
    {
        if (_storeIsReady)
            return;

        await store.GetJobAsync(Guid.Empty, cancellationToken);
        _storeIsReady = true;
    }

    public JobEnqueuer EnqueuerIn(DbTransaction transaction) => new(client, transaction);
}
