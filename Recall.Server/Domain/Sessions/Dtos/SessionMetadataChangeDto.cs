namespace Recall.Server.Domain.Sessions.Dtos;

using Resources;

public sealed record SessionMetadataChangeDto
{
    [SchemaMaxLength(typeof(Session), nameof(Session.MetadataValueMaxLength))]
    public string? Activity { get; init; }
    [SchemaMaxLength(typeof(Session), nameof(Session.MetadataValueMaxLength))]
    public IReadOnlyList<string> Moods { get; init; } = [];
    [SchemaMaxLength(typeof(Session), nameof(Session.MetadataValueMaxLength))]
    public IReadOnlyList<string> UserTopics { get; init; } = [];

    /// <summary>The <c>metadataVersion</c> of the Session that the client read. A save from another version gets 409, unless the Session already holds these values.</summary>
    public required uint MetadataVersion { get; init; }
}
