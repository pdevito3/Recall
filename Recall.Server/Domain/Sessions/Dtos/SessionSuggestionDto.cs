namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record SessionSuggestionDto
{
    [EnumNames<SuggestionKind>]
    public string Kind { get; init; } = default!;
    public string Value { get; init; } = default!;
}
