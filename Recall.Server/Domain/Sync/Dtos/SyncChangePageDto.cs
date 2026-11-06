namespace Recall.Server.Domain.Sync.Dtos;

using Corrections.Dtos;
using People.Dtos;
using Sessions.Dtos;
using UserSettings.Dtos;

/// <summary>Each changed row of one page, in its newest state.</summary>
public sealed record SyncChangePageDto
{
    /// <summary>Send it as the cursor of the next pull.</summary>
    public string Cursor { get; init; } = default!;

    /// <summary>True when a next page already exists, so pull again with no wait.</summary>
    public bool HasMore { get; init; }
    public IReadOnlyList<SessionDto> Sessions { get; init; } = [];
    public IReadOnlyList<PersonDto> People { get; init; } = [];
    public IReadOnlyList<CorrectionDto> Corrections { get; init; } = [];
    public UserSettingDto? Settings { get; init; }
    public IReadOnlyList<SyncTombstoneDto> Tombstones { get; init; } = [];
}
