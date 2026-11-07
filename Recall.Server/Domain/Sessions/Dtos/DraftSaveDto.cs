namespace Recall.Server.Domain.Sessions.Dtos;

using System.Text.Json.Serialization;
using Resources;

public sealed record DraftSaveDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(Session), nameof(Session.DraftMaxLength))]
    public string Text { get; init; } = default!;

    /// <summary>The number of the Draft that the person edited. It is <c>0</c> for a Draft with no Revision. Only the number of the current Draft wins.</summary>
    [SchemaMinimum(typeof(DraftSlot), nameof(DraftSlot.NoRevision))]
    public required int BaseRevision { get; init; }

    /// <summary>When the person saved. This time decides no save. With the words, it says that a save is a replay, so a replay sends the same time, and a new save sends a new time.</summary>
    [JsonConverter(typeof(InstantWithOffsetConverter))]
    public required DateTimeOffset SavedAt { get; init; }
}
