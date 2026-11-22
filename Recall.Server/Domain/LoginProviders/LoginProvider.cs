namespace Recall.Server.Domain.LoginProviders;

using Auth;
using Auth.Entities;
using Exceptions;

public enum LoginProviderState
{
    Usable,
    SecretUnreadable,
    ReplacedByEnvironment,
}

/// <summary>
/// An OIDC provider that an Admin added in the app (spec <c>login-providers.md</c>). It is install-wide, so it has no tenant, and the audit
/// log records each change as an action. The state is not stored: <see cref="StateOf"/> reads it from the entries of
/// <c>Auth:Providers</c> and from the key ring. Each change moves <see cref="Version"/>, the concurrency token of the row.
/// </summary>
[NotAudited("An install-wide row that holds a client secret. The audit log records each add, update, and delete as an action, and the row must hold no value that an audit entry copies.")]
public sealed class LoginProvider
{
    public const int IdMaxLength = AuthSession.LoginModeMaxLength;
    public const int DisplayNameMaxLength = 100;
    public const int IssuerMaxLength = ExternalLogin.IssuerMaxLength;
    public const int ClientIdMaxLength = 255;
    public const int ClientSecretMaxLength = 1_000;
    public const int CaCertificatePathMaxLength = 1_000;

    // The protected form of the longest secret, in base64 with the header of the key ring.
    public const int ProtectedClientSecretMaxLength = 4_000;

    public const string HttpIssuerMessage = "Give an issuer URL that starts with https://, or turn on Allow HTTP.";
    public const string UnreadableCertificateMessage = "The server cannot read a certificate at this path.";
    public const string IssuerHasQueryMessage = "Give an issuer URL with no query and no fragment.";
    public const string IdInUseMessage = "Another provider uses this ID. Pick another.";
    public const string IssuerInUseMessage = "Another provider uses this issuer.";
    public const string IssuerIsFixedMessage = "The issuer of a provider cannot change. Add a new provider instead.";

    public const string IdField = "id";
    public const string DisplayNameField = "displayName";
    public const string IssuerField = "issuer";
    public const string ClientIdField = "clientId";
    public const string ClientSecretField = "clientSecret";
    public const string CaCertificatePathField = "caCertificatePath";

    private LoginProvider()
    {
    }

    public string Id { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public string Issuer { get; private set; } = default!;
    public string ClientId { get; private set; } = default!;

    /// <summary>The client secret under the key ring. Only <see cref="LoginProviderSecret"/> reads it.</summary>
    public string ProtectedClientSecret { get; private set; } = default!;

    public bool AllowHttp { get; private set; }
    public string? CaCertificatePath { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public uint Version { get; private set; }

    /// <summary>A new row. <paramref name="protectedSecret"/> is the secret that <see cref="ClientSecretOf"/> checked, under the key ring.</summary>
    public static LoginProvider Add(LoginProviderForm form, string id, string protectedSecret, DateTimeOffset now)
    {
        var provider = new LoginProvider { Id = IdOf(id), Issuer = IssuerOf(form.Issuer, form.AllowHttp), CreatedAt = now, Version = 1 };
        provider.Apply(form, protectedSecret);
        return provider;
    }

    /// <summary>A null secret keeps the stored secret (IDP-4). The issuer cannot change (IDP-6).</summary>
    public void Update(LoginProviderForm form, string? protectedSecret, uint version)
    {
        if (version != Version)
            throw new StaleVersionException();

        ValidationException.Must(form.Issuer is null || SameIssuer(form.Issuer.Trim(), Issuer), IssuerField, IssuerIsFixedMessage);
        IssuerOf(Issuer, form.AllowHttp);
        Apply(form, protectedSecret ?? ProtectedClientSecret);
        Version++;
    }

    /// <summary>
    /// An entry of <c>Auth:Providers</c> with the ID or the issuer of this row wins, so the row is Replaced by the environment (IDP-3).
    /// Else a secret that the key ring cannot read makes the row Secret unreadable (IDP-5).
    /// </summary>
    public LoginProviderState StateOf(IReadOnlyList<OidcProviderOptions> environment, bool secretIsReadable)
        => environment.Select((entry, position) => (Id: AuthOptions.ModeIdOf(entry, position), entry.Issuer))
            .Any(entry => entry.Id == Id || SameIssuer(entry.Issuer, Issuer))
            ? LoginProviderState.ReplacedByEnvironment
            : secretIsReadable ? LoginProviderState.Usable : LoginProviderState.SecretUnreadable;

    /// <summary>The settings of a login with this row, the shape that an entry of <c>Auth:Providers</c> has.</summary>
    /// <param name="clientSecret">The secret that the key ring gave.</param>
    /// <param name="allowLoopback">The value of <c>Auth:AllowLoopbackProviders</c>.</param>
    public OidcProviderOptions OptionsWith(string clientSecret, bool allowLoopback) => new()
    {
        RefusesLoopback = !allowLoopback,
        Id = Id,
        DisplayName = DisplayName,
        Issuer = Issuer,
        ClientId = ClientId,
        ClientSecret = clientSecret,
        AllowHttp = AllowHttp,
        CaCertificatePath = CaCertificatePath,
    };

    /// <summary>A trailing slash is the one difference that providers show between the setting and the discovery document.</summary>
    public static bool SameIssuer(string? a, string? b)
        => a is not null && b is not null && string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.Ordinal);

    public static bool IsValidId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= IdMaxLength && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The checks of a secret that a person sends, before it goes under the key ring.</summary>
    public static string? ClientSecretOf(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return null;

        var trimmed = secret.Trim();
        ValidationException.ThrowWhenLongerThan(trimmed, ClientSecretMaxLength, ClientSecretField, $"A client secret can have no more than {ClientSecretMaxLength} characters.");
        ValidationException.Must(trimmed.All(c => c is > ' ' and < '\u007f'), ClientSecretField, "The client secret has a character that is not allowed.");
        return trimmed;
    }

    /// <summary>The issuer of a form, checked as an entry of <c>Auth:Providers</c> is (IDP-2), with no trailing slash.</summary>
    public static string IssuerOf(string? issuer, bool allowHttp)
    {
        ValidationException.ThrowWhenNullOrWhitespace(issuer, IssuerField, "Give the issuer URL of the provider.");
        var trimmed = issuer!.Trim();
        ValidationException.ThrowWhenLongerThan(trimmed, IssuerMaxLength, IssuerField, $"An issuer URL can have no more than {IssuerMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(trimmed, IssuerField, "The issuer URL has a character that is not allowed.");
        ValidationException.Must(Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http", IssuerField, HttpIssuerMessage);
        ValidationException.Must(parsed!.Scheme == "https" || allowHttp, IssuerField, HttpIssuerMessage);
        // The well-known path goes after the issuer, so a query or a fragment would send discovery to another URL.
        ValidationException.Must(trimmed.IndexOfAny(['?', '#']) < 0, IssuerField, IssuerHasQueryMessage);
        // One form of each issuer, so the unique index holds against two adds that differ only by a trailing slash (IDP-3).
        return trimmed.TrimEnd('/');
    }

    /// <summary>A path to a file of PEM certificates that this server can read, or null for none (IDP-2).</summary>
    public static string? CaCertificatePathOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var trimmed = path.Trim();
        ValidationException.ThrowWhenLongerThan(trimmed, CaCertificatePathMaxLength, CaCertificatePathField, $"A certificate path can have no more than {CaCertificatePathMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(trimmed, CaCertificatePathField, "The certificate path has a character that is not allowed.");
        ValidationException.Must(CaCertificateFile.Read(trimmed) is not null, CaCertificatePathField, UnreadableCertificateMessage);
        return trimmed;
    }

    private static string IdOf(string? id)
    {
        ValidationException.ThrowWhenNullOrWhitespace(id, IdField, "Give the provider an ID. It is the name of the provider in the sign-in URLs.");
        ValidationException.Must(IsValidId(id), IdField, $"An ID can have no more than {IdMaxLength} letters, digits, dashes, or underscores.");
        ValidationException.Must(id != PasswordLoginMode.ModeId, IdField, "This ID names the password login. Pick another.");
        return id!;
    }

    private void Apply(LoginProviderForm form, string protectedSecret)
    {
        ValidationException.ThrowWhenNullOrWhitespace(form.DisplayName, DisplayNameField, "Give the provider a name. The sign-in page shows it.");
        var displayName = form.DisplayName!.Trim();
        ValidationException.ThrowWhenLongerThan(displayName, DisplayNameMaxLength, DisplayNameField, $"A name can have no more than {DisplayNameMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(displayName, DisplayNameField, "The name has a character that is not allowed.");

        ValidationException.ThrowWhenNullOrWhitespace(form.ClientId, ClientIdField, "Give the client ID that the provider gave Recall.");
        var clientId = form.ClientId!.Trim();
        ValidationException.ThrowWhenLongerThan(clientId, ClientIdMaxLength, ClientIdField, $"A client ID can have no more than {ClientIdMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(clientId, ClientIdField, "The client ID has a character that is not allowed.");

        DisplayName = displayName;
        ClientId = clientId;
        AllowHttp = form.AllowHttp;
        CaCertificatePath = CaCertificatePathOf(form.CaCertificatePath);
        ProtectedClientSecret = protectedSecret;
    }

    public override string ToString() => $"[login provider {Id}]";
}

/// <summary>The fields of the add and the update form. <see cref="Issuer"/> is null in an update that keeps the issuer.</summary>
public sealed record LoginProviderForm(string? DisplayName, string? Issuer, string? ClientId, bool AllowHttp, string? CaCertificatePath);

public enum LoginProviderSecretState
{
    Readable,
    Unreadable,
}

public enum LoginProviderTestFailure
{
    Unreachable,
    NotDiscovery,
    OtherIssuer,
    NoS256,
}
