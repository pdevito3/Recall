namespace Recall.Server.Domain.UserSettings;

using Exceptions;
using TimeZones;

public enum TimeZoneMode
{
    FollowDevice,
    Fixed,
}

/// <summary>Where the zone of the Journaling day comes from: the device of the person, or a zone that the person picked.</summary>
public abstract record TimeZoneSetting
{
    private const string ModeField = "timeZoneMode";
    private const string ZoneField = "timeZone";

    /// <summary>The zone that the device reported last. It is null until the first report, and the day then comes from UTC.</summary>
    public sealed record FollowDevice(string? ReportedZone) : TimeZoneSetting
    {
        public override TimeZoneMode Mode => TimeZoneMode.FollowDevice;
        public override string? Zone => ReportedZone;
    }

    /// <summary>A zone that the person picked. A report of a device does not change it.</summary>
    public sealed record Fixed(string FixedZone) : TimeZoneSetting
    {
        public override TimeZoneMode Mode => TimeZoneMode.Fixed;
        public override string? Zone => FixedZone;
    }

    private TimeZoneSetting() { }

    public abstract TimeZoneMode Mode { get; }

    public abstract string? Zone { get; }

    public static TimeZoneSetting Of(TimeZoneMode mode, string? zone) => mode switch
    {
        TimeZoneMode.Fixed => new Fixed(zone!),
        _ => new FollowDevice(zone),
    };

    /// <summary>
    /// The setting of a request. A Fixed setting needs a zone. A FollowDevice setting can bring the zone of the device now, and with
    /// no zone it keeps the zone that holds now. <see cref="ValidatedExceptTheStoredZone"/> checks the zone.
    /// </summary>
    /// <exception cref="ValidationException">The mode is not a mode, or a Fixed setting has no zone.</exception>
    public static TimeZoneSetting Parse(string? mode, string? zone, TimeZoneSetting current)
    {
        ValidationException.Must(Enum.TryParse<TimeZoneMode>(mode, out var parsed) && Enum.GetName(parsed) == mode,
            ModeField, "The time zone must follow the device or be a zone that you pick.");

        var given = string.IsNullOrWhiteSpace(zone) ? null : zone;
        if (parsed == TimeZoneMode.Fixed)
            ValidationException.Must(given is not null, ZoneField, "Please pick a time zone.");

        return Of(parsed, given ?? current.Zone);
    }

    /// <summary>
    /// This setting, when its zone is an IANA zone or the zone that holds now.
    /// </summary>
    /// <exception cref="ValidationException">The zone is not an IANA zone, and it is not the zone that holds now.</exception>
    public TimeZoneSetting ValidatedExceptTheStoredZone(TimeZoneSetting current)
    {
        if (Zone is not null && Zone != current.Zone)
            TimeZoneId.Of(Zone);

        return this;
    }

    /// <summary>The setting after a device reports its zone. A Fixed zone does not change.</summary>
    /// <exception cref="ValidationException">The zone is not an IANA zone.</exception>
    public TimeZoneSetting WithReportedZone(string zone) => this switch
    {
        FollowDevice => new FollowDevice(TimeZoneId.Of(zone).Value),
        _ => this,
    };
}
