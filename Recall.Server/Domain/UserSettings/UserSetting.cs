namespace Recall.Server.Domain.UserSettings;

using Exceptions;
using TimeZones;

public class UserSetting : BaseEntity, ITenantable, IJournalOwned
{
    public const string IdPrefix = "ust";

    private static readonly TimeZoneId Utc = TimeZoneId.Of("UTC");

    [NotSensitive] public string TenantId { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [Sensitive] public string? TimeZone { get; private set; }

    public bool LocationCaptureEnabled { get; private set; }

    public bool RequirePeopleProposalApproval { get; private set; } = true;

    public static UserSetting DefaultsFor(string tenantId, string userId) => new() { TenantId = tenantId, UserId = userId };

    public bool TimeZoneIsKnown => IsKnownZone(TimeZone);

    public static bool IsKnownZone(string? timeZone) => timeZone is null || TimeZoneId.TryOf(timeZone) is not null;

    public static TimeZoneId ZoneOf(string? timeZone) => TimeZoneId.TryOf(timeZone) ?? Utc;

    /// <returns>False for a retry: the row holds these values and the client sent another version.</returns>
    /// <exception cref="StaleVersionException">The row changed after the version that the client read.</exception>
    /// <exception cref="ValidationException">The time zone is not an IANA zone, and it is not the stored zone.</exception>
    public bool Change(string? timeZone, bool locationCaptureEnabled, bool requirePeopleProposalApproval, uint version)
    {
        var zone = string.IsNullOrWhiteSpace(timeZone) ? null : timeZone;
        var isStored = zone == TimeZone && locationCaptureEnabled == LocationCaptureEnabled && requirePeopleProposalApproval == RequirePeopleProposalApproval;
        if (isStored && version != Version)
            return false;

        GuardVersion(version);

        // A client sends the whole row back, so a stored zone that this host does not know must not stop a change of the other settings.
        TimeZone = zone is null || zone == TimeZone ? zone : TimeZoneId.Of(zone).Value;
        LocationCaptureEnabled = locationCaptureEnabled;
        RequirePeopleProposalApproval = requirePeopleProposalApproval;
        return true;
    }

    protected UserSetting() : base(IdPrefix) { }
}
