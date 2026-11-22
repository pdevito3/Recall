namespace Recall.Server.Auth;

using Microsoft.Extensions.Options;

public static class AuthServices
{
    public static void AddAuthCore(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AuthOptions.SectionName);
        services.AddOptions<AuthOptions>()
            .Bind(section)
            // The binder drops a list element with a value that cannot convert, and says nothing. Alone, an element throws.
            .Configure(options => options.Providers =
            [
                .. section.GetSection(nameof(AuthOptions.Providers)).GetChildren()
                    .Select(element => element.Get<OidcProviderOptions>() ?? new OidcProviderOptions()),
            ])
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddSingleton<OidcHandlerFactory>(OidcLoginMode.DefaultHandler);
        services.AddSingleton<LoginModes>();
        services.AddSingleton<Domain.LoginProviders.LoginProviderSecret>();
        // Before SetupCodeAtBoot, which counts the rows that this load reads.
        services.AddHostedService<LoginModesAtBoot>();
        services.AddSingleton(provider => new OwnerClaim(OwnerClaim.PathFor(provider.GetRequiredService<IConfiguration>()), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<Provisioner>();
        services.AddSingleton<PasswordHashing>();
        services.AddSingleton<PasswordBackoff>();
        services.AddSingleton<SetupCode>();
        services.AddHostedService<SetupCodeAtBoot>();
        services.AddScoped<SessionStore>();
        services.AddHostedService<AuthSweepService>();
    }
}
