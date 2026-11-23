namespace Recall.Server.Domain.Sessions.Dtos;

public sealed record PersonProposalDto
{
    public string Label { get; init; } = default!;
    /// <summary>
    /// Each Person whose label matched the name when the Cleanup ran. Read the details of each from the People API. With two or
    /// more, an approve must name one in <c>bindToPersonId</c> or send <c>createNew</c>.
    /// </summary>
    public IReadOnlyList<string> MatchedPersonIds { get; init; } = [];

    /// <summary>True when the directory had no match when the Cleanup ran. An approval still looks at the directory of that moment.</summary>
    public bool IsNew { get; init; }

    /// <summary>The first sentences of Cleaned that an approval tags, as previews. The number of previews and the width of each have a limit.</summary>
    public IReadOnlyList<string> Contexts { get; init; } = [];
}
