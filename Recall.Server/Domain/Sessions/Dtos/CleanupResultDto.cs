namespace Recall.Server.Domain.Sessions.Dtos;

using People;
using Resources;

/// <summary>A Cleanup that a model on the device did. The server does the same work on it as on the answer of a model provider.</summary>
public sealed record CleanupResultDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(Session), nameof(Session.DraftMaxLength))]
    public string CleanedMarkdown { get; init; } = default!;

    [SchemaMaxLength(typeof(Session), nameof(Session.SynopsisMaxLength))]
    public string? Synopsis { get; init; }

    [SchemaMaxLength(typeof(Session), nameof(Session.MetadataValueMaxLength))]
    public IReadOnlyList<string> TopicSuggestions { get; init; } = [];

    [SchemaMaxLength(typeof(Session), nameof(Session.MetadataValueMaxLength))]
    public IReadOnlyList<string> MoodSuggestions { get; init; } = [];

    [SchemaMaxLength(typeof(Person), nameof(Person.LabelMaxLength))]
    public IReadOnlyList<string> PeopleProposal { get; init; } = [];

    /// <summary>The Revision of Raw that the model read. A later Revision of Raw makes the Session Stale.</summary>
    public required int BaseRawRevision { get; init; }
}
