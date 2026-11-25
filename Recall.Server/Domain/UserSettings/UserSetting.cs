namespace Recall.Server.Domain.UserSettings;

using Exceptions;
using TimeZones;

public class UserSetting : BaseEntity, ITenantable, IJournalOwned
{
    public const string IdPrefix = "ust";

    [NotSensitive] public string TenantId { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    private TimeZoneMode TimeZoneMode { get; set; }

    [Sensitive] private string? TimeZoneValue { get; set; }

    public TimeZoneSetting TimeZone
    {
        get => TimeZoneSetting.Of(TimeZoneMode, TimeZoneValue);
        private set => (TimeZoneMode, TimeZoneValue) = (value.Mode, value.Zone);
    }

    public bool LocationCaptureEnabled { get; private set; }

    public bool RequirePeopleProposalApproval { get; private set; } = true;

    public bool RemoveFillerWords { get; private set; } = true;

    public bool WriteNumbersAsDigits { get; private set; } = true;

    public bool RequireImportReview { get; private set; } = true;

    public static UserSetting DefaultsFor(string tenantId, string userId) => new() { TenantId = tenantId, UserId = userId };

    public bool TimeZoneIsKnown => TimeZoneValue is null || TimeZoneId.TryOf(TimeZoneValue) is not null;

    public TimeZoneId JournalZone => TimeZoneId.OrUtc(TimeZoneValue);

    /// <exception cref="ValidationException">The mode is not a mode, or the time zone is not an IANA zone and it is not the stored zone.</exception>
    public bool Change(string? timeZoneMode, string? timeZone, bool locationCaptureEnabled, bool requirePeopleProposalApproval, bool removeFillerWords, bool writeNumbersAsDigits, uint version)
    {
        var zone = TimeZoneSetting.Parse(timeZoneMode, timeZone, TimeZone);
        var isStored = zone == TimeZone
            && locationCaptureEnabled == LocationCaptureEnabled
            && requirePeopleProposalApproval == RequirePeopleProposalApproval
            && removeFillerWords == RemoveFillerWords
            && writeNumbersAsDigits == WriteNumbersAsDigits;
        if (isStored && version != Version)
            return false;

        GuardVersion(version);

        TimeZone = zone.ValidatedExceptTheStoredZone(TimeZone);
        LocationCaptureEnabled = locationCaptureEnabled;
        RequirePeopleProposalApproval = requirePeopleProposalApproval;
        RemoveFillerWords = removeFillerWords;
        WriteNumbersAsDigits = writeNumbersAsDigits;
        return true;
    }

    public bool ChangeImportReview(bool requireImportReview, uint version)
    {
        var isStored = requireImportReview == RequireImportReview;
        if (isStored && version != Version)
            return false;

        GuardVersion(version);
        RequireImportReview = requireImportReview;
        return true;
    }

    /// <summary>A device reports the zone that it is in. Only a row that follows the device takes it.</summary>
    /// <exception cref="ValidationException">The zone is not an IANA zone.</exception>
    public bool ReportDeviceZone(string zone)
    {
        var next = TimeZone.WithReportedZone(zone);
        if (next == TimeZone)
            return false;

        TimeZone = next;
        return true;
    }

    protected UserSetting() : base(IdPrefix) { }
}
