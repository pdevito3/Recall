namespace Recall.UnitTests.Auth;

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Resources.Entrypoints.Auth;
using Serilog.Events;
using Serilog.Parsing;
using Shouldly;

public class SignInBoundaryTests
{
    [Test]
    [Arguments("recall://auth/callback", true)]
    [Arguments("http://127.0.0.1:1/", true)]
    [Arguments("http://127.0.0.1:65535/cb", true)]
    [Arguments("http://[::1]:7000/a/b-c_d.e~f", true)]
    [Arguments("http://127.0.0.1:65536/cb", false)]
    [Arguments("http://127.0.0.1:0/cb", false)]
    [Arguments("http://127.0.0.1:01/cb", false)]
    [Arguments("http://127.0.0.1/cb", false)]
    [Arguments("http://127.0.0.1:7000", false)]
    [Arguments("http://127.0.0.1:7000/cb?x=1", false)]
    [Arguments("http://127.0.0.1:7000/cb#x", false)]
    [Arguments("http://127.0.0.1:7000/%2e%2e", false)]
    [Arguments("http://u@127.0.0.1:7000/cb", false)]
    [Arguments("http://127.0.0.1.evil.test:7000/cb", false)]
    [Arguments("http://localhost:7000/cb", false)]
    [Arguments("http://[::2]:7000/cb", false)]
    [Arguments("https://127.0.0.1:7000/cb", false)]
    [Arguments("HTTP://127.0.0.1:7000/cb", false)]
    [Arguments("recall://auth/callback/", false)]
    [Arguments("recall://auth/callback?x=1", false)]
    [Arguments("RECALL://auth/callback", false)]
    [Arguments("recall://other/callback", false)]
    [Arguments("other://auth/callback", false)]
    [Arguments("https://evil.test/cb", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public void native_redirect_table(string? raw, bool valid)
    {
        // Act
        var parsed = NativeRedirect.TryParse(raw, out var redirect);

        // Assert
        parsed.ShouldBe(valid);
        if (valid)
            redirect!.Value.ShouldBe(raw);
    }

    [Test]
    public void a_native_redirect_over_the_limit_does_not_parse()
    {
        // Arrange
        var raw = "http://127.0.0.1:7000/" + new string('a', LoginAttempt.NativeRedirectMaxLength);

        // Act
        var parsed = NativeRedirect.TryParse(raw, out _);

        // Assert
        parsed.ShouldBeFalse();
    }

    [Test]
    public void a_native_redirect_escapes_the_values_of_its_query()
    {
        // Arrange
        NativeRedirect.TryParse(NativeRedirect.AppCallback, out var redirect).ShouldBeTrue();

        // Act
        var url = redirect!.WithQuery(("code", "a b"), ("state", "x&y=z"));

        // Assert
        url.ShouldBe("recall://auth/callback?code=a%20b&state=x%26y%3Dz");
    }

    [Test]
    [Arguments("/", "/")]
    [Arguments("/entries/1?tab=2#x", "/entries/1?tab=2#x")]
    [Arguments("/a//b", "/a//b")]
    [Arguments("/café", "/caf%C3%A9")]
    [Arguments(null, "/")]
    [Arguments("", "/")]
    [Arguments("entries", "/")]
    [Arguments("https://evil.test/", "/")]
    [Arguments("//evil.test/", "/")]
    [Arguments("/\\evil.test", "/")]
    [Arguments("\\\\evil.test", "/")]
    [Arguments("/a\\b", "/")]
    [Arguments("/\t/evil.test", "/")]
    [Arguments("/\r\nSet-Cookie: x=1", "/")]
    [Arguments("/\u0000", "/")]
    [Arguments("/\ud800", "/")]
    [Arguments("javascript:alert(1)", "/")]
    public void local_return_path_table(string? raw, string expected)
    {
        // Act
        var path = LocalReturnPath.Of(raw);

        // Assert
        path.ShouldBe(expected);
    }

    [Test]
    public void a_return_path_over_the_limit_gives_the_root()
    {
        // Arrange
        var atLimit = "/" + new string('a', LocalReturnPath.MaxLength - 1);

        // Act
        var kept = LocalReturnPath.Of(atLimit);
        var dropped = LocalReturnPath.Of(atLimit + "a");
        var droppedAfterEncoding = LocalReturnPath.Of("/" + new string('é', LocalReturnPath.MaxLength / 2));

        // Assert
        kept.ShouldBe(atLimit);
        dropped.ShouldBe("/");
        droppedAfterEncoding.ShouldBe("/");
    }

    [Test]
    [Arguments("Bearer abc", "abc")]
    [Arguments("bearer abc", "abc")]
    [Arguments("BEARER abc", "abc")]
    [Arguments("Bearer  abc ", "abc")]
    [Arguments("Bearer ", null)]
    [Arguments("Bearer", null)]
    [Arguments("Basic abc", null)]
    [Arguments("Negotiate abc", null)]
    [Arguments("", null)]
    public void credential_table(string authorization, string? expected)
    {
        // Act
        var credential = AppSessionHandler.CredentialOf(authorization);

        // Assert
        credential.ShouldBe(expected);
    }

    private static DefaultHttpContext RequestWith(string? authorization, string? cookie, bool https = false)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = https ? "https" : "http";
        if (authorization is not null)
            http.Request.Headers.Authorization = authorization;
        if (cookie is not null)
            http.Request.Headers.Cookie = cookie;
        return http;
    }

    [Test]
    [Arguments("Bearer rcl_at_x", null, true, AppSessionHandler.SchemeName)]
    [Arguments("bearer rcl_at_x", null, true, AppSessionHandler.SchemeName)]
    [Arguments("Bearer eyJ.x.y", null, true, JwtBearerDefaults.AuthenticationScheme)]
    [Arguments("Bearer eyJ.x.y", "recall=rcl_ck_x", true, JwtBearerDefaults.AuthenticationScheme)]
    [Arguments("Basic abc", "recall=rcl_ck_x", true, AppSessionHandler.SchemeName)]
    [Arguments("basic abc", "recall=rcl_ck_x", true, AppSessionHandler.SchemeName)]
    [Arguments("Basic abc", null, true, JwtBearerDefaults.AuthenticationScheme)]
    [Arguments(null, "recall=rcl_ck_x", true, AppSessionHandler.SchemeName)]
    [Arguments(null, "other=x", true, JwtBearerDefaults.AuthenticationScheme)]
    [Arguments(null, null, true, JwtBearerDefaults.AuthenticationScheme)]
    [Arguments(null, null, false, AppSessionHandler.SchemeName)]
    [Arguments("Bearer eyJ.x.y", null, false, AppSessionHandler.SchemeName)]
    public void selector_table(string? authorization, string? cookie, bool jwtOn, string expected)
    {
        // Arrange
        var http = RequestWith(authorization, cookie);

        // Act
        var scheme = AuthRegistration.SelectScheme(http.Request, jwtOn);

        // Assert
        scheme.ShouldBe(expected);
    }

    [Test]
    [Property("Rule", "PAT-10")]
    [Arguments("web", true)]
    [Arguments("native", true)]
    [Arguments("pat", false)]
    [Arguments(null, false)]
    public async Task only_a_web_or_native_session_passes_the_interactive_session_policy(string? clientKind, bool passes)
    {
        // Arrange
        await using var services = new ServiceCollection().AddLogging().AddAuthorization().BuildServiceProvider();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        List<Claim> claims = [new("sub", "usr_1"), new(AppSessionHandler.SessionIdClaim, "ses_1")];
        if (clientKind is not null)
            claims.Add(new Claim(AppSessionHandler.ClientKindClaim, clientKind));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AppSessionHandler.SchemeName));

        // Act
        var result = await authorization.AuthorizeAsync(principal, AuthEndpoints.InteractiveSessionOnly);

        // Assert
        result.Succeeded.ShouldBe(passes);
    }

    [Test]
    public void on_https_the_selector_reads_only_the_host_prefix_name()
    {
        // Arrange
        var plain = RequestWith(null, "recall=rcl_ck_x", https: true);
        var secure = RequestWith(null, "__Host-recall=rcl_ck_x", https: true);

        // Act
        var forPlain = AuthRegistration.SelectScheme(plain.Request, jwtOn: true);
        var forSecure = AuthRegistration.SelectScheme(secure.Request, jwtOn: true);

        // Assert
        forPlain.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
        forSecure.ShouldBe(AppSessionHandler.SchemeName);
    }

    [Test]
    public void pkce_matches_only_the_verifier_of_the_challenge()
    {
        // Arrange
        var verifier = Secrets.New();
        var challenge = Pkce.ChallengeOf(verifier);

        // Act & Assert
        Pkce.Matches(verifier, challenge).ShouldBeTrue();
        Pkce.Matches(Secrets.New(), challenge).ShouldBeFalse();
        Pkce.Matches("short", Pkce.ChallengeOf("short")).ShouldBeFalse();
        Pkce.Matches(new string('a', Pkce.VerifierMaxLength + 1), Pkce.ChallengeOf(new string('a', Pkce.VerifierMaxLength + 1))).ShouldBeFalse();
    }

    [Test]
    public void pkce_challenge_is_the_value_in_rfc_7636_appendix_b()
    {
        // Act
        var challenge = Pkce.ChallengeOf("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        // Assert
        challenge.ShouldBe("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    private static LogEvent Line(string source, string path)
        => new(
            TimeProvider.System.GetUtcNow(),
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Request"),
            [
                new LogEventProperty(Serilog.Core.Constants.SourceContextPropertyName, new ScalarValue(source)),
                new LogEventProperty("Path", new ScalarValue(path)),
            ]);

    [Test]
    [Arguments("Microsoft.AspNetCore.Hosting.Diagnostics", "/auth/oidc/callback", false)]
    [Arguments("Microsoft.AspNetCore.Hosting.Diagnostics", "/auth/token", false)]
    [Arguments("Microsoft.AspNetCore.Hosting.Diagnostics", "/api/v1/tenants", true)]
    [Arguments("Serilog.AspNetCore.RequestLoggingMiddleware", "/auth/token", true)]
    public void the_log_filter_drops_only_the_framework_request_lines_of_sign_in(string source, string path, bool kept)
    {
        // Arrange
        var filter = new AuthQueryLogFilter();

        // Act
        var enabled = filter.IsEnabled(Line(source, path));

        // Assert
        enabled.ShouldBe(kept);
    }

    private static OidcProviderOptions GoodProvider() => new()
    {
        Id = "mode",
        Issuer = "https://idp.test",
        ClientId = "client",
        ClientSecret = "value",
    };

    [Test]
    public void no_provider_and_a_good_provider_are_valid_settings()
    {
        // Arrange
        var validator = new AuthOptionsValidator();

        // Act
        var none = validator.Validate(null, new AuthOptions());
        var one = validator.Validate(null, new AuthOptions { Providers = [GoodProvider()] });

        // Assert
        none.Succeeded.ShouldBeTrue();
        one.Succeeded.ShouldBeTrue();
    }

    [Test]
    [Arguments("id", "no space", "Auth:Providers:0:Id")]
    [Arguments("issuer", "not a url", "Auth:Providers:0:Issuer")]
    [Arguments("issuer", "http://idp.test", "AllowHttp")]
    [Arguments("clientId", "", "ClientId is missing")]
    [Arguments("clientSecret", "", "ClientSecret is missing")]
    [Arguments("ca", "/no/such/file", "CaCertificatePath")]
    public void a_typo_in_a_provider_names_the_setting(string field, string value, string expected)
    {
        // Arrange
        var provider = GoodProvider();
        _ = field switch
        {
            "id" => provider.Id = value,
            "issuer" => provider.Issuer = value,
            "clientId" => provider.ClientId = value,
            "clientSecret" => provider.ClientSecret = value,
            _ => provider.CaCertificatePath = value,
        };

        // Act
        var result = new AuthOptionsValidator().Validate(null, new AuthOptions { Providers = [provider] });

        // Assert
        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldContain(expected);
    }

    [Test]
    public void a_file_with_no_certificate_names_the_setting()
    {
        // Arrange
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "not a certificate");
        var provider = GoodProvider();
        provider.CaCertificatePath = path;

        // Act
        var result = new AuthOptionsValidator().Validate(null, new AuthOptions { Providers = [provider] });
        File.Delete(path);

        // Assert
        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldContain("CaCertificatePath");
    }

    [Test]
    public void a_login_limit_under_one_names_the_setting()
    {
        // Act
        var result = new AuthOptionsValidator().Validate(null, new AuthOptions { StartPermitsPerWindow = 0 });

        // Assert
        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldContain("StartPermitsPerWindow");
    }

    [Test]
    public void two_providers_with_one_id_name_the_repeat()
    {
        // Arrange
        var options = new AuthOptions { Providers = [GoodProvider(), GoodProvider()] };

        // Act
        var result = new AuthOptionsValidator().Validate(null, options);

        // Assert
        result.FailureMessage!.ShouldContain("repeats");
    }

    [Test]
    public void to_string_of_each_sign_in_type_hides_its_secrets()
    {
        // Arrange
        NativeRedirect.TryParse(NativeRedirect.AppCallback, out var redirect).ShouldBeTrue();
        var secret = Secrets.New();
        var native = new LoginClient.Native(redirect!, secret, secret);
        var answer = new AuthEndpoints.TokenAnswer(secret, secret, "Bearer", 900);
        var web = new WebSession("ses", secret, TimeProvider.System.GetUtcNow());
        var attempt = new LoginAttempt
        {
            StateHash = secret,
            BindingHash = secret,
            ModeId = "mode",
            Nonce = secret,
            PkceVerifier = secret,
            RedirectUri = "http://localhost/auth/oidc/callback",
            ClientKind = SessionKind.Native,
            NativeChallenge = secret,
            NativeState = secret,
            ExpiresAt = TimeProvider.System.GetUtcNow(),
        };
        var code = new AppCode
        {
            CodeHash = secret,
            UserId = "usr",
            LoginMode = "mode",
            RedirectUri = NativeRedirect.AppCallback,
            Challenge = secret,
            ClientState = secret,
            ExpiresAt = TimeProvider.System.GetUtcNow(),
        };
        var provider = new OidcProviderOptions { Id = "mode", ClientSecret = secret };
        var callback = new OidcCallback(secret, secret, secret);

        // Act
        string[] printed = [$"{native}", $"{answer}", $"{web}", $"{attempt}", $"{code}", $"{provider}", $"{callback}"];

        // Assert
        printed.ShouldAllBe(text => !text.Contains(secret, StringComparison.Ordinal));
    }
}
