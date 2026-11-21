namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Recall.Server.Auth;
using Serilog.Core;

public static class AuthRegistration
{
    public const string PolicyScheme = "Recall";
    public const string StartLimiter = "auth-start";

    /// <summary>The limit of a page that writes a login attempt. It has the buckets of the start limit, and its own permits, so a page draws none from a start.</summary>
    public const string PageLimiter = "auth-page";

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
            options.OnRejected = (context, _) => LoginStartLimit.RefuseAsync(context, StartWindow);
            options.AddPolicy(StartLimiter, PerAddress);
            options.AddPolicy(PageLimiter, PerAddress);
        });

        services.AddScoped<LoginCompletion>();
        services.AddSingleton<PasswordLogin>();
        services.AddSingleton<ILogEventFilter, AuthQueryLogFilter>();
        return services;
    }

    private static RateLimitPartition<string> PerAddress(HttpContext http)
        => RateLimitPartition.GetFixedWindowLimiter(ConnectionAddress.BucketOf(http.Connection.RemoteIpAddress), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.StartPermitsPerWindow,
            Window = StartWindow,
            QueueLimit = 0,
        });

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
        endpoints.MapPasswordEndpoints();
    }
}
