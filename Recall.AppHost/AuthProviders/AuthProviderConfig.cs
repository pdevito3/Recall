using Microsoft.Extensions.Configuration;

namespace Recall.AppHost.AuthProviders;

/// <summary>
/// Configuration for an authentication provider.
/// </summary>
public sealed record AuthProviderConfig
{
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string Audience { get; init; }
    public required bool RequireHttpsMetadata { get; init; }
    public required string NameClaimType { get; init; }
    public required string RoleClaimType { get; init; }

    /// <summary>
    /// Static authority URL for external providers (e.g., Duende Demo).
    /// </summary>
    public string? Authority { get; init; }

    /// <summary>
    /// Container resource for local providers (e.g., FusionAuth).
    /// When set, the Authority will be derived from this container's endpoint.
    /// </summary>
    public IResourceBuilder<ContainerResource>? AuthResource { get; init; }

    /// <summary>
    /// Whether to include the Audience as an OAuth scope.
    /// Some providers (like Keycloak) require the audience as a scope to get proper access tokens.
    /// FusionAuth doesn't support custom scopes and will reject unknown scopes.
    /// Default is true for compatibility with most providers.
    /// </summary>
    public bool UseAudienceAsScope { get; init; } = true;

    /// <summary>
    /// Path suffix to append to the container endpoint for the authority URL.
    /// Used by providers like Keycloak that include the realm in the authority path.
    /// Example: "/realms/aspire" for Keycloak.
    /// </summary>
    public string? AuthorityPathSuffix { get; init; }

    /// <summary>
    /// The access token claim that the API compares with the Audience.
    /// Cognito access tokens have no aud claim. They put the app client ID in client_id.
    /// </summary>
    public string AudienceClaimType { get; init; } = "aud";

    /// <summary>
    /// More environment variables for the server only, for a provider that needs a server setting that no other
    /// provider needs.
    /// </summary>
    public IReadOnlyDictionary<string, string> ServerSettings { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// The app URLs that a local identity provider must accept as redirect targets.
/// An isolated run gives these endpoints random ports, so the seed files read them from environment variables.
/// <para><paramref name="ServerHttp"/> is the server http endpoint, for a login in Swagger UI on that endpoint.</para>
/// </summary>
public sealed record AuthClientUrls(
    EndpointReference Frontend,
    EndpointReference Server,
    EndpointReference ServerHttp)
{
    public const string FrontendVariable = "APP_FRONTEND_URL";
    public const string ServerVariable = "APP_SERVER_URL";
    public const string ServerHttpVariable = "APP_SERVER_HTTP_URL";
}

/// <summary>
/// Extension methods for configuring resources with auth provider settings.
/// </summary>
public static class AuthProviderExtensions
{
    /// <summary>
    /// Gives the Server the provider as its login method (<c>Auth:Providers:0</c>) and as its JWT authority
    /// (<c>Auth:Authority</c>), which Swagger, MCP, gRPC, and agents use.
    /// </summary>
    public static IResourceBuilder<T> WithServerAuth<T>(
        this IResourceBuilder<T> resource,
        AuthProviderConfig authProvider,
        IAuthProvider provider) where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        resource
            .WithEnvironment("Auth__ClientId", authProvider.ClientId)
            .WithEnvironment("Auth__ClientSecret", authProvider.ClientSecret)
            .WithEnvironment("Auth__Audience", authProvider.Audience)
            .WithEnvironment("Auth__RequireHttpsMetadata", authProvider.RequireHttpsMetadata.ToString())
            .WithEnvironment("Auth__NameClaimType", authProvider.NameClaimType)
            .WithEnvironment("Auth__RoleClaimType", authProvider.RoleClaimType)
            .WithEnvironment("Auth__UseAudienceAsScope", authProvider.UseAudienceAsScope.ToString())
            .WithEnvironment("Auth__AudienceClaimType", authProvider.AudienceClaimType)
            .WithEnvironment("Auth__Providers__0__Id", provider.Name.ToLowerInvariant())
            .WithEnvironment("Auth__Providers__0__DisplayName", provider.Name)
            .WithEnvironment("Auth__Providers__0__ClientId", authProvider.ClientId)
            .WithEnvironment("Auth__Providers__0__ClientSecret", authProvider.ClientSecret)
            .WithEnvironment("Auth__Providers__0__AllowHttp", (!authProvider.RequireHttpsMetadata).ToString());

        foreach (var (name, value) in authProvider.ServerSettings)
        {
            resource.WithEnvironment(name, value);
        }

        // A login reads the discovery document of the provider, so the Server waits for its health check
        if (authProvider.AuthResource is not null)
            resource.WaitFor(authProvider.AuthResource);

        resource.WithEnvironment(context =>
        {
            // A static URL, or the container endpoint with the path of the realm
            var issuer = authProvider.AuthResource is { } container
                ? ReferenceExpression.Create($"{container.GetEndpoint("http")}{authProvider.AuthorityPathSuffix}")
                : ReferenceExpression.Create($"{authProvider.Authority}");
            context.EnvironmentVariables["Auth__Authority"] = issuer;
            context.EnvironmentVariables["Auth__Providers__0__Issuer"] = issuer;
        });

        return resource;
    }

    /// <summary>
    /// Gives the Server the login settings of the AppHost configuration, for a stack with no provider of its own.
    /// With no <c>Auth:Providers</c> the Server boots with no login method.
    /// </summary>
    public static IResourceBuilder<T> WithForwardedAuth<T>(
        this IResourceBuilder<T> resource,
        IConfiguration configuration) where T : IResourceWithEnvironment
    {
        var settings = configuration.GetSection("Auth:Providers").AsEnumerable()
            .Append(new("Auth:OpenSignup", configuration["Auth:OpenSignup"]))
            .Where(setting => setting.Value is not null);
        foreach (var (key, value) in settings)
            resource.WithEnvironment(key.Replace(":", "__", StringComparison.Ordinal), value);

        return resource;
    }

    /// <summary>
    /// Gives the app URLs to an identity provider container for its redirect URI seed data.
    /// </summary>
    public static IResourceBuilder<ContainerResource> WithClientUrls(
        this IResourceBuilder<ContainerResource> container,
        AuthClientUrls clientUrls)
    {
        // Url is the host view (http://localhost:<port>) that the browser uses. A plain endpoint reference
        // in a container environment resolves to the container network view instead.
        return container.WithEnvironment(context =>
        {
            context.EnvironmentVariables[AuthClientUrls.FrontendVariable] = clientUrls.Frontend.Url;
            context.EnvironmentVariables[AuthClientUrls.ServerVariable] = clientUrls.Server.Url;
            context.EnvironmentVariables[AuthClientUrls.ServerHttpVariable] = clientUrls.ServerHttp.Url;
        });
    }
}
