namespace Recall.Server.Domain.Sessions.Dtos;

using System.Text.Json.Serialization;
using Resources;

public sealed record SessionMetadataChangeDto
{
    [SchemaMaxLength(typeof(Session), nameof(Session.MetadataValueMaxLength))]
    public string? Activity { get; init; }
    [SchemaMaxLength(typeof(Session), nameof(Session.MetadataValueMaxLength))]
    public IReadOnlyList<string> Moods { get; init; } = [];
    [SchemaMaxLength(typeof(Session), nameof(Session.MetadataValueMaxLength))]
    public IReadOnlyList<string> UserTopics { get; init; } = [];

    /// <summary>The time of the edit on the client. A change with a time that is not newer than the stored change does nothing.</summary>
    [JsonConverter(typeof(InstantWithOffsetConverter))]
    public required DateTimeOffset SavedAt { get; init; }
}
