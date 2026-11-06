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

    /// <summary>The Person to tag. When this is null, the Person is the match of the proposal, then the Person of this label, then a new Person.</summary>
    [SchemaMaxLength(typeof(EntityId), nameof(EntityId.MaxLength))]
    public string? BindToPersonId { get; init; }

    /// <summary>True adds a Person with this label, also when one has that label. Send it only when a person asks for a second one.</summary>
    public bool CreateNew { get; init; }
}
