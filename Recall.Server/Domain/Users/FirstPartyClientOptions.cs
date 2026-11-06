namespace Recall.Server.Domain.Users;

public sealed class FirstPartyClientOptions
{
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
