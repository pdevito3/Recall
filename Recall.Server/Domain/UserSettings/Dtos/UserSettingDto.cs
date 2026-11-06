namespace Recall.Server.Domain.UserSettings.Dtos;

public sealed record UserSettingDto
{
    public string? TimeZone { get; init; }

    /// <summary>False when this host does not know the stored zone. The Journaling day then comes from UTC until the person sets a zone.</summary>
    public bool TimeZoneIsKnown { get; init; }
    public bool LocationCaptureEnabled { get; init; }
    public bool RequirePeopleProposalApproval { get; init; }
    public uint Version { get; init; }
}
