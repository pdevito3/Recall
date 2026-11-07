namespace Recall.Server.Domain.Sessions.Dtos;

using System.ComponentModel.DataAnnotations;

public sealed record WaitingCleanupApplyDto
{
    /// <summary>The Cleaned Revision that holds the waiting result, from <c>waitingCleanupRevision</c> of the Session.</summary>
    [Range(1, int.MaxValue)]
    public required int Revision { get; init; }

    /// <summary>The number of the Cleaned Draft that the person read.</summary>
    [Range(0, int.MaxValue)]
    public required int BaseCleanedRevision { get; init; }
}
