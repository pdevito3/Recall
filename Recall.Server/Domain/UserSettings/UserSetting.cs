namespace Recall.Server.Domain.UserSettings;

using Exceptions;
using Sessions;
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

    [Sensitive] public DateTimeOffset? SavedAt { get; private set; }

    [Sensitive] public IReadOnlyList<long> FastClaims { get; private set; } = [];

    public static UserSetting DefaultsFor(string tenantId, string userId) => new() { TenantId = tenantId, UserId = userId };

    public bool TimeZoneIsKnown => IsKnownZone(TimeZone);

    public static bool IsKnownZone(string? timeZone) => timeZone is null || TimeZoneId.TryOf(timeZone) is not null;

    public static TimeZoneId ZoneOf(string? timeZone) => TimeZoneId.TryOf(timeZone) ?? Utc;

    /// <returns>False when the save time is not newer, and the change does nothing.</returns>
    /// <exception cref="ValidationException">The time zone is not an IANA zone, and it is not the stored zone.</exception>
    public bool Change(string? timeZone, bool locationCaptureEnabled, bool requirePeopleProposalApproval, DateTimeOffset claimedSavedAt, DateTimeOffset now)
    {
        if (!claimedSavedAt.IsNewer(SavedAt, FastClaims, now))
            return false;

        // A client sends the whole row back, so a stored zone that this host does not know must not stop a change of the other settings.
        var zone = string.IsNullOrWhiteSpace(timeZone) ? null : timeZone == TimeZone ? timeZone : TimeZoneId.Of(timeZone).Value;
        var savedAt = FastClockClaims.SaveTimeOf(claimedSavedAt, now);
        TimeZone = zone;
        LocationCaptureEnabled = locationCaptureEnabled;
        RequirePeopleProposalApproval = requirePeopleProposalApproval;
        SavedAt = savedAt;
        FastClaims = FastClaims.Remembering(claimedSavedAt, savedAt);
        return true;
    }

    protected UserSetting() : base(IdPrefix) { }
}
