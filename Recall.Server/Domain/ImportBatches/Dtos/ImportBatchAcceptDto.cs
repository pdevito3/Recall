namespace Recall.Server.Domain.ImportBatches.Dtos;

public sealed record ImportBatchAcceptDto
{
    /// <summary>The version of the batch that the person reviewed. An accept from another version gets 409.</summary>
    public required uint Version { get; init; }
}
