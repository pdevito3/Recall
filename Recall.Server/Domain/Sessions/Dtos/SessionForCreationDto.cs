namespace Recall.Server.Domain.Sessions.Dtos;

using System.Text.Json.Serialization;
using Resources;
using TimeZones;

public sealed record SessionForCreationDto
{
    /// <summary>The ID that the device made for this Session. A second create with this ID returns the same Session.</summary>
    [RequiredField]
    [SchemaMaxLength(typeof(Session), nameof(Session.ClientSessionIdMaxLength))]
    public string ClientSessionId { get; init; } = default!;

    /// <summary>A time in the future becomes the time of the server.</summary>
    [JsonConverter(typeof(InstantWithOffsetConverter))]
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// The IANA zone that the device used for the day when the person started the Session: the zone that the person picked, or the
    /// zone of the device. Null, or a zone that this host does not know, gives the zone of the settings.
    /// </summary>
    [SchemaMaxLength(typeof(TimeZoneId), nameof(TimeZoneId.MaxLength))]
    public string? TimeZone { get; init; }

    /// <summary>The server does not store a location when capture is off in the settings of the User.</summary>
    public double? Latitude { get; init; }

    public double? Longitude { get; init; }
}
