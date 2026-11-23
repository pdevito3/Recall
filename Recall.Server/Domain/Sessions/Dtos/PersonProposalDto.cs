namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record PersonProposalDto
{
    public string Label { get; init; } = default!;
    /// <summary>
    /// Each Person that an approve with no choice reads now: each match of the Cleanup that still exists, then each other Person
    /// of the name. Read the details of each from the People API. With two or more, an approve must name one in
    /// <c>bindToPersonId</c> or send <c>createNew</c>.
    /// </summary>
    public IReadOnlyList<string> MatchedPersonIds { get; init; } = [];

    /// <summary>True when <see cref="MatchedPersonIds"/> is empty, so a plain approve adds a Person of the name.</summary>
    public bool IsNew { get; init; }

    /// <summary>The first sentences of Cleaned that an approval tags, as previews. The number of previews and the width of each have a limit.</summary>
    public IReadOnlyList<string> Contexts { get; init; } = [];
}
