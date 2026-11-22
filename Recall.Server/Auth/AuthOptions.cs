namespace Recall.Server.Auth;

using Entities;
using Microsoft.Extensions.Options;

public sealed class OidcProviderOptions
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? Issuer { get; set; }
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>A file with the certificates of a private CA. This provider alone trusts it, and the machine store stays as it is.</summary>
    public string? CaCertificatePath { get; set; }

    public bool AllowHttp { get; set; }

    /// <summary>
    /// True for a provider that an Admin added in the app: its client refuses to connect to a loopback, link-local, or unspecified
    /// address, so an issuer cannot point discovery at this server or at a metadata service. No setting binds it.
    /// </summary>
    public bool RefusesLoopback { get; internal set; }

    public override string ToString() => $"[provider {Id}]";
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public List<OidcProviderOptions> Providers { get; set; } = [];

    /// <summary>True lets a person with no user join the oldest tenant as a User. The default closes sign-up after the Owner.</summary>
    public bool OpenSignup { get; set; }

    /// <summary>How many login starts the server takes in each 10 seconds, over all callers. A start writes a row and can call the provider.</summary>
    public int StartPermitsPerWindow { get; set; } = 20;

    /// <summary>True lets a provider from the app connect to a loopback, link-local, or unspecified address. A provider of <see cref="Providers"/> always can.</summary>
    public bool AllowLoopbackProviders { get; set; }

    public static string ModeIdOf(OidcProviderOptions provider, int position)
        => string.IsNullOrWhiteSpace(provider.Id) ? $"oidc{position}" : provider.Id;
}

public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var failures = new List<string>();
        if (options.StartPermitsPerWindow < 1)
            failures.Add($"Auth:{nameof(AuthOptions.StartPermitsPerWindow)} must be 1 or more.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenIssuers = new HashSet<string>(StringComparer.Ordinal);
        for (var position = 0; position < options.Providers.Count; position++)
        {
            var provider = options.Providers[position];
            var key = $"Auth:Providers:{position}";
            var id = AuthOptions.ModeIdOf(provider, position);
            if (id == PasswordLoginMode.ModeId)
                failures.Add($"{key}:Id is '{id}', which names the password login. Give the provider another ID.");
            if (!seen.Add(id))
                failures.Add($"{key}:Id repeats the ID '{id}'. Give each provider its own ID.");
            if (id.Length > AuthSession.LoginModeMaxLength || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                failures.Add($"{key}:Id must have no more than {AuthSession.LoginModeMaxLength} letters, digits, dashes, or underscores.");
            if (string.IsNullOrWhiteSpace(provider.ClientId))
                failures.Add($"{key}:ClientId is missing.");
            if (string.IsNullOrWhiteSpace(provider.ClientSecret))
                failures.Add($"{key}:ClientSecret is missing.");
            if (!string.IsNullOrWhiteSpace(provider.CaCertificatePath) && CaCertificateFile.Read(provider.CaCertificatePath) is null)
                failures.Add($"{key}:CaCertificatePath must name a file of PEM certificates.");

            if (!Uri.TryCreate(provider.Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme is not ("https" or "http"))
                failures.Add($"{key}:Issuer must be an absolute http or https URL.");
            else if (issuer.Scheme == "http" && !provider.AllowHttp)
                failures.Add($"{key}:Issuer uses http, and {key}:AllowHttp is not true.");
            // A link names its issuer, so two entries with one issuer would share each link (IDP-3).
            else if (!seenIssuers.Add(provider.Issuer!.TrimEnd('/')))
                failures.Add($"{key}:Issuer repeats the issuer of another provider. Give each provider its own issuer.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
