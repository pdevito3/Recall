namespace Recall.Server.Auth;

using Microsoft.Extensions.Options;

public static class AuthServices
{
    /// <summary>The core of sign-in, with no transport. <c>AddRecallAuthentication</c> adds the HTTP half.</summary>
    public static void AddAuthCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection(AuthOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddSingleton<OidcHandlerFactory>(OidcLoginMode.DefaultHandler);
        services.AddSingleton<LoginModes>();
        services.AddSingleton(provider => new OwnerClaim(OwnerClaim.PathFor(provider.GetRequiredService<IConfiguration>())));
        services.AddSingleton<Provisioner>();
        services.AddScoped<SessionStore>();
        services.AddHostedService<AuthSweepService>();
    }
}
