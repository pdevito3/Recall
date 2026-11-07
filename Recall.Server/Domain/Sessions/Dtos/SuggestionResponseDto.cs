namespace Recall.Server.Domain.Sessions.Dtos;

using Resources;

public sealed record SuggestionResponseDto
{
    [RequiredField]
    [EnumNames<SuggestionKind>]
    public string Kind { get; init; } = default!;

    [RequiredField]
    [SchemaMaxLength(typeof(Session), nameof(Session.MetadataValueMaxLength))]
    public string Value { get; init; } = default!;
}
