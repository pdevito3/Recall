namespace Recall.Server.Domain.Sessions.Dtos;

using System.Text.Json.Serialization;
using Resources;

public sealed record DraftSaveDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(Session), nameof(Session.DraftMaxLength))]
    public string Text { get; init; } = default!;

    /// <summary>The Revision that the person edited. It is <c>0</c> for a Draft with no Revision.</summary>
    [SchemaMinimum(typeof(DraftSlot), nameof(DraftSlot.NoRevision))]
    public required int BaseRevision { get; init; }

    /// <summary>When the person saved. A time in the future becomes the time of the server. A replay sends the same time. A merge after <c>Lost</c> is a new save and sends a new time.</summary>
    [JsonConverter(typeof(InstantWithOffsetConverter))]
    public required DateTimeOffset SavedAt { get; init; }
}
