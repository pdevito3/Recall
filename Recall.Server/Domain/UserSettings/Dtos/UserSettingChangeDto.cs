namespace Recall.Server.Domain.UserSettings.Dtos;

using Resources;
using TimeZones;

public sealed record UserSettingChangeDto
{
    /// <summary>The IANA time zone that gives the Journaling day. Null means UTC. The stored zone is always allowed, also when this host does not know it.</summary>
    [SchemaMaxLength(typeof(TimeZoneId), nameof(TimeZoneId.MaxLength))]
    public string? TimeZone { get; init; }
    public required bool LocationCaptureEnabled { get; init; }
    public required bool RequirePeopleProposalApproval { get; init; }

    /// <summary>The <c>version</c> of the settings that the client read. A save from another version gets 409, unless the row already holds these values.</summary>
    public required uint Version { get; init; }
}
