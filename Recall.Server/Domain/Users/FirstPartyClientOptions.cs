namespace Recall.Server.Domain.Users;

/// <summary>
/// The client IDs of the first-party apps: <see cref="AppClientId"/>, the client of each app session, and
/// <c>Auth:ClientId</c> and <c>Swagger:ClientId</c> when they are set. A token from one of these clients acts as its
/// user, with all of the user's permissions.
/// </summary>
public sealed class FirstPartyClientOptions
{
    /// <summary>The client of an app session. A provider token that names it is refused at the JWT handler.</summary>
    public const string AppClientId = "recall";

    public required IReadOnlySet<string> ClientIds { get; init; }

    public bool Contains(string clientId) => ClientIds.Contains(clientId);

    public static FirstPartyClientOptions FromConfiguration(IConfiguration configuration)
    {
        var clientIds = new HashSet<string>(StringComparer.Ordinal) { AppClientId };
        if (configuration["Auth:ClientId"] is { } authClientId && !string.IsNullOrWhiteSpace(authClientId))
            clientIds.Add(authClientId);
        if (configuration["Swagger:ClientId"] is { } swaggerClientId && !string.IsNullOrWhiteSpace(swaggerClientId))
            clientIds.Add(swaggerClientId);

        return new FirstPartyClientOptions { ClientIds = clientIds };
    }
}
