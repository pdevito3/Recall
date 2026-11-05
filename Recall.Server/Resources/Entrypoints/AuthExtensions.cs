namespace Recall.Server.Resources.Entrypoints;

using System.Security.Claims;
using Domain.Users;
using DPoP;
using Mcp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Extension methods for configuring API authentication.
/// </summary>
public static class AuthExtensions
{
    /// <summary>
    /// Adds the JWT half of authentication, for an install with <c>Auth:Authority</c>. Then <c>Auth:Audience</c> is
    /// required. Reads Auth:Authority, Auth:Audience, Auth:RequireHttpsMetadata, Auth:NameClaimType, and
    /// Auth:RoleClaimType from configuration.
    /// </summary>
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
                        if (NamesAnAppSession(context.Principal))
                        {
                            context.Fail("A provider token cannot name an app session.");
                        }
                        else if (!validatesAud && context.Principal?.FindFirst(audienceClaimType)?.Value != authAudience)
                        {
                            context.Fail($"The token has no '{audienceClaimType}' claim with the value '{authAudience}'.");
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        // DPoP (RFC 9449): the DPoP scheme, the proof check, and the rejection of a bound token sent as Bearer
        services.AddDPoP(configuration);
        // After AddDPoP, so that its challenge wraps the DPoP challenge and can put the Bearer challenge first
        services.AddMcpResourceMetadata(configuration);

        return services;
    }

    // Each user who can hold a session has an identifier with the user ID prefix. So a provider that lets a person pick
    // a sub, or a client ID, cannot act as a Recall user.
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
