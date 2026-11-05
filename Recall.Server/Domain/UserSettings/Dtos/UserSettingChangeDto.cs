namespace Recall.Server.Domain.UserSettings.Dtos;

using Resources;
using TimeZones;

public sealed record UserSettingChangeDto
{
    /// <summary>The IANA time zone that gives the Journaling day. Null means UTC.</summary>
    [SchemaMaxLength(typeof(TimeZoneId), nameof(TimeZoneId.MaxLength))]
    public string? TimeZone { get; init; }
    public required bool LocationCaptureEnabled { get; init; }
    public required bool RequirePeopleTagApproval { get; init; }

    /// <summary>When the person made the change. A change with an older save time than the stored settings does nothing.</summary>
    public required DateTimeOffset SavedAt { get; init; }
}
