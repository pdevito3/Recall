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

    public bool RequirePeopleTagApproval { get; private set; } = true;

    [Sensitive] public DateTimeOffset? SavedAt { get; private set; }

    [Sensitive] public IReadOnlyList<long> FastClaims { get; private set; } = [];

    public static UserSetting DefaultsFor(string tenantId, string userId) => new() { TenantId = tenantId, UserId = userId };

    public static TimeZoneId ZoneOf(string? timeZone) => timeZone is null ? Utc : TimeZoneId.Of(timeZone);

    /// <exception cref="ValidationException">The time zone is not an IANA zone.</exception>
    public void Change(string? timeZone, bool locationCaptureEnabled, bool requirePeopleTagApproval, DateTimeOffset claimedSavedAt, DateTimeOffset now)
    {
        var zone = string.IsNullOrWhiteSpace(timeZone) ? null : TimeZoneId.Of(timeZone).Value;

        if (!claimedSavedAt.IsNewer(SavedAt, FastClaims, now))
            return;

        var savedAt = FastClockClaims.SaveTimeOf(claimedSavedAt, now);
        TimeZone = zone;
        LocationCaptureEnabled = locationCaptureEnabled;
        RequirePeopleTagApproval = requirePeopleTagApproval;
        SavedAt = savedAt;
        FastClaims = FastClaims.Remembering(claimedSavedAt, savedAt);
    }

    protected UserSetting() : base(IdPrefix) { }
}
