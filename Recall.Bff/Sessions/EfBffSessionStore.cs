using Microsoft.EntityFrameworkCore;

namespace Recall.Bff.Sessions;

/// <summary>
/// A write with an old version changes no row, and EF Core then reports a concurrency failure.
/// </summary>
public sealed class EfBffSessionStore(BffDbContext dbContext, TimeProvider timeProvider) : IBffSessionStore
{
    public async Task<BffSession> CreateAsync(BffSession session, CancellationToken cancellationToken)
    {
        var entry = dbContext.Sessions.Add(session);
        entry.Property(e => e.Version).CurrentValue = 1;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return entry.Entity;
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }

    public Task<BffSession?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return dbContext.Sessions.AsNoTracking()
            .TagWith("bff_sessions.get")
            .Where(s => s.Key == key && s.Expires > now && s.AbsoluteExpires > now)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task<BffSessionWrite> RenewAsync(string key, byte[] ticket, DateTimeOffset renewed, DateTimeOffset expires,
        uint expectedVersion, CancellationToken cancellationToken) =>
        WriteAsync(key, expectedVersion, s => s with { Ticket = ticket, Renewed = renewed, Expires = expires },
            [nameof(BffSession.Ticket), nameof(BffSession.Renewed), nameof(BffSession.Expires)], cancellationToken);

    public Task<BffSessionWrite> RenewExpiryAsync(string key, DateTimeOffset renewed, DateTimeOffset expires,
        uint expectedVersion, CancellationToken cancellationToken) =>
        WriteAsync(key, expectedVersion, s => s with { Renewed = renewed, Expires = expires },
            [nameof(BffSession.Renewed), nameof(BffSession.Expires)], cancellationToken);

    /// <summary>
    /// One <c>DELETE ... RETURNING</c>, so that the returned row is the row that the delete removed.
    /// </summary>
    public async Task<BffSession?> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var deleted = await dbContext.Sessions
            .FromSql($"DELETE FROM sessions WHERE key = {key} RETURNING *")
            .AsNoTracking()
            .TagWith("bff_sessions.delete")
            .ToListAsync(cancellationToken);
        return deleted.SingleOrDefault();
    }

    public async Task<IReadOnlyList<BffSession>> FindBySubjectAsync(string subjectId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await dbContext.Sessions.AsNoTracking()
            .TagWith("bff_sessions.find_by_subject")
            .Where(s => s.SubjectId == subjectId && s.Expires > now && s.AbsoluteExpires > now)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BffSession>> FindBySessionIdAsync(string sessionId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await dbContext.Sessions.AsNoTracking()
            .TagWith("bff_sessions.find_by_session_id")
            .Where(s => s.SessionId == sessionId && s.Expires > now && s.AbsoluteExpires > now)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Two deletes, one for each expiry column. Each one takes its batch in the order of its column, so it reads
    /// only the start of the index on that column and stops at the batch size.
    /// </summary>
    public async Task<int> DeleteEndedAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        var deleted = await DeleteBatchAsync(dbContext.Sessions.Where(s => s.Expires <= now).OrderBy(s => s.Expires),
            "bff_sessions.delete_ended_sliding", batchSize, cancellationToken);
        if (deleted < batchSize)
            deleted += await DeleteBatchAsync(
                dbContext.Sessions.Where(s => s.AbsoluteExpires <= now).OrderBy(s => s.AbsoluteExpires),
                "bff_sessions.delete_ended_absolute", batchSize - deleted, cancellationToken);
        return deleted;
    }

    private Task<int> DeleteBatchAsync(IQueryable<BffSession> ended, string tag, int batchSize, CancellationToken cancellationToken)
    {
        var batch = ended.Take(batchSize).Select(s => s.Key);
        return dbContext.Sessions
            .TagWith(tag)
            .Where(s => batch.Contains(s.Key))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<BffSessionWrite> WriteAsync(string key, uint expectedVersion, Func<BffSession, BffSession> change,
        string[] properties, CancellationToken cancellationToken)
    {
        // A stub with the expected version. EF Core writes only the properties that are marked as modified.
        var stub = change(new BffSession(key, "", null, default, default, default, default, [], expectedVersion));
        var entry = dbContext.Sessions.Attach(stub);
        foreach (var property in properties)
            entry.Property(property).IsModified = true;
        entry.Property(e => e.Version).CurrentValue = expectedVersion + 1;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new BffSessionWrite.Written(entry.Entity.Version);
        }
        catch (DbUpdateConcurrencyException)
        {
            var current = await GetAsync(key, cancellationToken);
            return current is null ? BffSessionWrite.Missing.Instance : new BffSessionWrite.Conflict(current);
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }
}
