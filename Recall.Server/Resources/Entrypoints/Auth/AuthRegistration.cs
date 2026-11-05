namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Recall.Server.Auth;
using Serilog.Core;

/// <summary>The default scheme, the conditional JWT half, and the routes of sign-in.</summary>
public static class AuthRegistration
{
    public const string PolicyScheme = "Recall";
    public const string StartLimiter = "auth-start";

    private static readonly TimeSpan StartWindow = TimeSpan.FromSeconds(10);

    public static bool JwtIsOn(IConfiguration configuration) => !string.IsNullOrWhiteSpace(configuration["Auth:Authority"]);

    public static IServiceCollection AddRecallAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var jwtOn = JwtIsOn(configuration);
        if (jwtOn)
            services.AddJwtBearerAuthentication(configuration, environment);

        services.AddAuthentication(PolicyScheme)
            .AddPolicyScheme(PolicyScheme, null, options => options.ForwardDefaultSelector = http => SelectScheme(http.Request, jwtOn))
            .AddScheme<AuthenticationSchemeOptions, AppSessionHandler>(AppSessionHandler.SchemeName, null);
        services.AddAuthorization();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(StartLimiter, http => RateLimitPartition.GetFixedWindowLimiter("start", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.StartPermitsPerWindow,
                Window = StartWindow,
                QueueLimit = 0,
            }));
        });

        services.AddScoped<LoginCompletion>();
        services.AddSingleton<ILogEventFilter, AuthQueryLogFilter>();
        return services;
    }

    /// <summary>
    /// A scheme name compares with no case. Only Bearer and DPoP are credentials here, so nginx Basic or a proxy header
    /// never hides the cookie.
    /// </summary>
    public static string SelectScheme(HttpRequest request, bool jwtOn)
    {
        var token = AppSessionHandler.CredentialOf(request.Headers.Authorization.ToString());
        if (token is not null && token.StartsWith(AppSessionHandler.AppTokenPrefix, StringComparison.Ordinal))
            return AppSessionHandler.SchemeName;
        if (token is null && request.Cookies.ContainsKey(SessionCookie.NameFor(request)))
            return AppSessionHandler.SchemeName;
        return jwtOn ? JwtBearerDefaults.AuthenticationScheme : AppSessionHandler.SchemeName;
    }

    public static void MapRecallAuth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAuthEndpoints();
        endpoints.MapOidcEndpoints();
        endpoints.MapBffShim();
    }
}
