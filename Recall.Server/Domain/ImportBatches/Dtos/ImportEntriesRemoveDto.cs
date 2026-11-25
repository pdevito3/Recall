namespace Recall.Server.Domain.ImportBatches.Dtos;

using Resources;
using Sessions;

public sealed record ImportEntriesRemoveDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(Session), nameof(Session.ClientSessionIdMaxLength))]
    public IReadOnlyList<string> ClientSessionIds { get; init; } = [];
}

public sealed record ImportRemoveResultDto
{
    /// <summary>The version of the batch after the remove. A remove that deletes nothing does not move it.</summary>
    public uint Version { get; init; }

    /// <summary>The outcome of each ID, in the order of the call, each ID once.</summary>
    public IReadOnlyList<ImportRemoveOutcomeDto> Outcomes { get; init; } = [];
}

public sealed record ImportRemoveOutcomeDto
{
    public string ClientSessionId { get; init; } = default!;

    [EnumNames<ImportRemoveOutcome>]
    public string Outcome { get; init; } = default!;
}
