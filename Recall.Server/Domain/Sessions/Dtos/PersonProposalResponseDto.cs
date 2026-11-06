namespace Recall.Server.Domain.Sessions.Dtos;

using People;
using Resources;

public sealed record PersonProposalResponseDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(Person), nameof(Person.LabelMaxLength))]
    public string Label { get; init; } = default!;

    /// <summary>False drops the proposal. True tags each place in Cleaned.</summary>
    public required bool Approve { get; init; }

    /// <summary>The Person to tag. When this is null, the match of the proposal is the Person.</summary>
    public string? BindToPersonId { get; init; }

    /// <summary>True adds a Person with this label, in place of a match.</summary>
    public bool CreateNew { get; init; }
}
