namespace Recall.Server.Domain.UserSettings.Dtos;

using Resources;
using TimeZones;

public sealed record DeviceTimeZoneDto
{
    /// <summary>The IANA time zone that the device is in now.</summary>
    [RequiredField]
    [SchemaMaxLength(typeof(TimeZoneId), nameof(TimeZoneId.MaxLength))]
    public string TimeZone { get; init; } = default!;
}
