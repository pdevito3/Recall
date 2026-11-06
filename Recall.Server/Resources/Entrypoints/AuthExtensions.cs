namespace Recall.Server.Resources.Entrypoints;

using System.Security.Claims;
using Domain.Users;
using Mcp;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
                        if (NamesAnAppSession(context.Principal))
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
