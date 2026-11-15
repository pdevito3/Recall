namespace Recall.Server.Domain.UserSettings.Dtos;

public sealed record UserSettingDto
{
    /// <summary>FollowDevice when the zone comes from the device of the person, or Fixed when the person picked it.</summary>
    [EnumNames<TimeZoneMode>]
    public string TimeZoneMode { get; init; } = default!;

    /// <summary>The zone that gives the Journaling day now: the zone that the person picked, or the zone that a device reported last. Null means UTC.</summary>
    public string? TimeZone { get; init; }

    /// <summary>False when this host does not know the stored zone. The Journaling day then comes from UTC until the person sets a zone.</summary>
    public bool TimeZoneIsKnown { get; init; }
    public bool LocationCaptureEnabled { get; init; }
    public bool RequirePeopleProposalApproval { get; init; }

    /// <summary>True when the Cleanup prompt asks the model to remove filler words, stutters, and false starts from Cleaned. Raw keeps each word.</summary>
    public bool RemoveFillerWords { get; init; }

    public uint Version { get; init; }
}
