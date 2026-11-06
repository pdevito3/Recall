namespace Recall.Server.Domain.UserSettings.Dtos;

using System.Text.Json.Serialization;
using Resources;
using TimeZones;

public sealed record UserSettingChangeDto
{
    /// <summary>The IANA time zone that gives the Journaling day. Null means UTC. The stored zone is always allowed, also when this host does not know it.</summary>
    [SchemaMaxLength(typeof(TimeZoneId), nameof(TimeZoneId.MaxLength))]
    public string? TimeZone { get; init; }
    public required bool LocationCaptureEnabled { get; init; }
    public required bool RequirePeopleTagApproval { get; init; }

    /// <summary>When the person made the change. A change with an older save time than the stored settings does nothing.</summary>
    [JsonConverter(typeof(InstantWithOffsetConverter))]
    public required DateTimeOffset SavedAt { get; init; }
}
