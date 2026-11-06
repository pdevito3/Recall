namespace Recall.Server.Domain.Sessions.Dtos;

using System.ComponentModel.DataAnnotations;
using Resources;

public sealed record CursorPageParametersDto
{
    public string? Cursor { get; init; }

    [Range(1, int.MaxValue)]
    public int PageSize { get; init; } = BasePaginationParameters.DefaultPageSizeValue;

    public static CursorPageParametersDto From(string? cursor, int? pageSize,
        int defaultPageSize = BasePaginationParameters.DefaultPageSizeValue, int limit = BasePaginationParameters.MaxPageSizeLimit) => new()
    {
        Cursor = string.IsNullOrEmpty(cursor) ? null : cursor,
        PageSize = Math.Min(pageSize ?? defaultPageSize, limit),
    };
}
