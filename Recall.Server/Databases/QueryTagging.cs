namespace Recall.Server.Databases;

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

public static class QueryTagging
{
    /// <summary>
    /// The first word of the comment that <see cref="TagWithOperationCallSite{T}"/> writes.
    /// </summary>
    internal const string QueryPrefix = "Recall.Query:";

    internal const string SuffixPrefix = "Recall.QuerySuffix:";

    /// <summary>
    /// Tags a query with <paramref name="operationName"/> plus the calling member, file, and line.
    /// </summary>
    /// <param name="source">The query to tag.</param>
    /// <param name="operationName">
    /// A stable dotted name in <c>area.operation</c> form, for example <c>users.get_by_id</c>.
    /// Keep it constant across refactors so dashboards and alerts stay valid. Never interpolate an id.
    /// </param>
    /// <param name="memberName">Filled by the compiler. Do not pass a value.</param>
    /// <param name="filePath">Filled by the compiler. Do not pass a value.</param>
    /// <param name="lineNumber">Filled by the compiler. Do not pass a value.</param>
    public static IQueryable<T> TagWithOperationCallSite<T>(
        this IQueryable<T> source,
        string operationName,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0) =>
        source
            .TagWith($"{QueryPrefix} {operationName}; Member: {memberName}")
            .TagWithCallSite(filePath, lineNumber);

    internal static IQueryable<T> TagAsCountQuery<T>(this IQueryable<T> source) =>
        source.TagWith($"{SuffixPrefix} count");
}
