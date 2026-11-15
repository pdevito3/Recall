namespace Recall.Server.Resources.Entrypoints;

using System.Security.Claims;
using Domain.Users;
using Mcp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Extension methods for configuring API authentication.
/// </summary>
public static class AuthExtensions
{
    public static IServiceCollection AddJwtBearerAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var authAuthority = configuration["Auth:Authority"]
            ?? throw new InvalidOperationException("Auth:Authority configuration is required.");
        var authAudience = configuration["Auth:Audience"]
            ?? throw new InvalidOperationException("Auth:Audience configuration is required.");

        // These have defaults for backwards compatibility
        var requireHttpsMetadata = configuration.GetValue("Auth:RequireHttpsMetadata", !environment.IsDevelopment());
        var nameClaimType = configuration["Auth:NameClaimType"] ?? "name";
        var roleClaimType = configuration["Auth:RoleClaimType"] ?? "role";
        // Cognito access tokens have no aud claim. They put the app client ID in client_id.
        var audienceClaimType = configuration["Auth:AudienceClaimType"] ?? "aud";
        var validatesAud = audienceClaimType == "aud";

        services.AddAuthentication()
            .AddJwtBearer(options =>
            {
                options.Authority = authAuthority;
                options.Audience = authAudience;

                options.RequireHttpsMetadata = requireHttpsMetadata;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = validatesAud,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    NameClaimType = nameClaimType,
                    RoleClaimType = roleClaimType
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (context.SecurityToken is JsonWebToken token && IsNotAnAccessToken(token))
                        {
                            context.Fail("The token is an ID token, a logout token, or a refresh token, not an access token.");
                        }
                        else if (NamesAnAppSession(context.Principal))
                        {
                            context.Fail("A provider token cannot name an app session.");
                        }
                        else if (context.Principal?.HasClaim(c => c.Type == "cnf") == true)
                        {
                            context.Fail("The token has a cnf claim, and the Server does not check a proof of possession.");
                        }
                        else if (!validatesAud && context.Principal?.FindFirst(audienceClaimType)?.Value != authAudience)
                        {
                            context.Fail($"The token has no '{audienceClaimType}' claim with the value '{authAudience}'.");
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddMcpResourceMetadata(configuration);

        return services;
    }

    // An ID token of the login client has that client ID as its aud, and a provider preset can give the API the same audience.
    // Its azp names a first-party client, so the Server would read it as a first-party user that acts as any user whose
    // identifier is its sub. The token type has no standard claim, so this reads the claim of each provider that has one.
    // An Authentik ID token has none, but it has no azp either, so CallerIdentityResolver takes it as a token of an unknown client.
    private static bool IsNotAnAccessToken(JsonWebToken token) =>
        // Keycloak: ID, Logout, or Refresh, and Bearer for an access token
        (token.TryGetPayloadValue<string>("typ", out var typ) && typ is "ID" or "Logout" or "Refresh")
        // FusionAuth: idt, and at for an access token
        || (token.TryGetPayloadValue<string>("tty", out var tty) && tty == "idt")
        // Cognito: id, and access for an access token
        || (token.TryGetPayloadValue<string>("token_use", out var tokenUse) && tokenUse == "id")
        // A back-channel logout token has an events claim (OpenID Connect Back-Channel Logout 1.0, section 2.4)
        || token.TryGetPayloadValue<object>("events", out _);

    private static bool NamesAnAppSession(ClaimsPrincipal? principal)
    {
        if (principal is null)
            return false;

        return principal.FindFirst("client_id")?.Value == FirstPartyClientOptions.AppClientId
            || principal.FindFirst("azp")?.Value == FirstPartyClientOptions.AppClientId
            || HasUserIdPrefix(principal.FindFirst("sub")?.Value)
            || HasUserIdPrefix(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    }

    private static bool HasUserIdPrefix(string? subject)
        => subject?.StartsWith(User.IdPrefix + "_", StringComparison.Ordinal) == true;
}
