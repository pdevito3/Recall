namespace Recall.Server.Domain.Sessions;

using Microsoft.EntityFrameworkCore;

public readonly record struct KeysetPage<T>(List<T> Rows, bool HasMore);

public static class KeysetPageExtensions
{
    public static async Task<KeysetPage<T>> ToKeysetPageAsync<T>(this IQueryable<T> query, int pageSize, CancellationToken cancellationToken)
    {
        var rows = await query.Take(pageSize + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > pageSize;
        if (hasMore)
            rows.RemoveAt(pageSize);
        return new KeysetPage<T>(rows, hasMore);
    }
}
