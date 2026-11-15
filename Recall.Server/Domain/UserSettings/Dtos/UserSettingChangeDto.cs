namespace Recall.Server.Domain.UserSettings.Dtos;

using Resources;
using TimeZones;

public sealed record UserSettingChangeDto
{
    /// <summary>FollowDevice to take the zone from the device, or Fixed to keep the zone in <c>timeZone</c>.</summary>
    [RequiredField]
    [EnumNames<TimeZoneMode>]
    public string TimeZoneMode { get; init; } = default!;

    /// <summary>
    /// For Fixed, the IANA time zone that the person picked. For FollowDevice, the zone of this device now, or null to keep the zone
    /// that holds. The stored zone is always allowed, also when this host does not know it.
    /// </summary>
    [SchemaMaxLength(typeof(TimeZoneId), nameof(TimeZoneId.MaxLength))]
    public string? TimeZone { get; init; }
    public required bool LocationCaptureEnabled { get; init; }
    public required bool RequirePeopleProposalApproval { get; init; }

    /// <summary>True to have the Cleanup prompt ask the model to remove filler words, stutters, and false starts from Cleaned. Null keeps the stored value, so a client that does not know this field does not change it.</summary>
    public bool? RemoveFillerWords { get; init; }

    /// <summary>The <c>version</c> of the settings that the client read. A save from another version gets 409, unless the row already holds these values.</summary>
    public required uint Version { get; init; }
}
