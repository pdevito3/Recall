namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record SessionTopicDto
{
    public string Name { get; init; } = default!;

    /// <summary>Who chose the Topic.</summary>
    [EnumNames<TopicProvenance>]
    public string Provenance { get; init; } = default!;
}
