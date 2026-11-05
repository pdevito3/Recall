namespace Recall.Server.Domain.Sessions.Dtos;

using Resources;

public sealed record SessionForCreationDto
{
    /// <summary>The ID that the device made for this Session. A second create with this ID returns the same Session.</summary>
    [RequiredField]
    [SchemaMaxLength(typeof(Session), nameof(Session.ClientSessionIdMaxLength))]
    public string ClientSessionId { get; init; } = default!;

    /// <summary>A time in the future becomes the time of the server.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The server does not store a location when capture is off in the settings of the User.</summary>
    public double? Latitude { get; init; }

    public double? Longitude { get; init; }
}
