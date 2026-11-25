namespace Recall.Server.Domain.UserSettings.Dtos;

public sealed record ImportReviewChangeDto
{
    /// <summary>True to let only a web session of the person accept an import batch. False to let each caller with journal write accept one.</summary>
    public required bool RequireImportReview { get; init; }

    /// <summary>The <c>version</c> of the settings that the client read. A save from another version gets 409, unless the row already holds this value.</summary>
    public required uint Version { get; init; }
}
