namespace Recall.UnitTests.Domain.LoginProviders;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Recall.Server.Auth;
using Recall.Server.Domain.LoginProviders;
using Recall.Server.Exceptions;
using Shouldly;

public class LoginProviderTests
{
    private static LoginProviderForm Form(string issuer = "https://idp.example", bool allowHttp = false, string? ca = null)
        => new("Example", issuer, "recall", allowHttp, ca);

    private static LoginProvider Row(string id = "example", string issuer = "https://idp.example")
        => LoginProvider.Add(Form(issuer), id, "protected", DateTimeOffset.UnixEpoch);

    [Test]
    [Property("Rule", "IDP-2")]
    [Arguments("password")]
    [Arguments("no space")]
    [Arguments("")]
    public void an_id_that_an_entry_of_the_environment_could_not_have_is_refused(string id)
    {
        // Act
        var add = () => LoginProvider.Add(Form(), id, "protected", DateTimeOffset.UnixEpoch);

        // Assert
        add.ShouldThrow<ValidationException>().Property.ShouldBe(LoginProvider.IdField);
    }

    [Test]
    [Property("Rule", "IDP-2")]
    public void an_id_of_more_than_its_limit_is_refused()
    {
        // Act
        var add = () => LoginProvider.Add(Form(), new string('a', LoginProvider.IdMaxLength + 1), "protected", DateTimeOffset.UnixEpoch);

        // Assert
        add.ShouldThrow<ValidationException>().Property.ShouldBe(LoginProvider.IdField);
    }

    [Test]
    [Property("Rule", "IDP-2")]
    [Arguments("http://idp.example", false)]
    [Arguments("not a url", false)]
    [Arguments("ftp://idp.example", true)]
    public void an_issuer_that_is_not_https_or_allowed_http_gets_the_message_of_the_field(string issuer, bool allowHttp)
    {
        // Act
        var add = () => LoginProvider.Add(Form(issuer, allowHttp), "example", "protected", DateTimeOffset.UnixEpoch);

        // Assert
        var refused = add.ShouldThrow<ValidationException>();
        refused.Property.ShouldBe(LoginProvider.IssuerField);
        refused.Message.ShouldBe(LoginProvider.HttpIssuerMessage);
    }

    [Test]
    [Property("Rule", "IDP-2")]
    public void an_http_issuer_with_allow_http_is_taken()
    {
        // Act
        var row = LoginProvider.Add(Form("http://idp.example", allowHttp: true), "example", "protected", DateTimeOffset.UnixEpoch);

        // Assert
        row.Issuer.ShouldBe("http://idp.example");
        row.AllowHttp.ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "IDP-2")]
    public void a_ca_path_that_holds_no_certificate_gets_the_message_of_the_field()
    {
        // Arrange
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "not a certificate");

        // Act
        var add = () => LoginProvider.Add(Form(ca: path), "example", "protected", DateTimeOffset.UnixEpoch);

        // Assert
        var refused = add.ShouldThrow<ValidationException>();
        File.Delete(path);
        refused.Property.ShouldBe(LoginProvider.CaCertificatePathField);
        refused.Message.ShouldBe(LoginProvider.UnreadableCertificateMessage);
    }

    [Test]
    [Property("Rule", "IDP-2")]
    public void a_ca_path_of_a_pem_file_is_taken()
    {
        // Arrange
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.GetTempFileName();
        File.WriteAllText(path, certificate.ExportCertificatePem());

        // Act
        var row = LoginProvider.Add(Form(ca: path), "example", "protected", DateTimeOffset.UnixEpoch);
        File.Delete(path);

        // Assert
        row.CaCertificatePath.ShouldBe(path);
    }

    [Test]
    [Property("Rule", "IDP-2")]
    public void a_ca_path_longer_than_its_column_is_refused_before_the_file_is_read()
    {
        // Act
        var add = () => LoginProvider.CaCertificatePathOf(new string('a', LoginProvider.CaCertificatePathMaxLength + 1));

        // Assert
        var refused = add.ShouldThrow<ValidationException>();
        refused.Property.ShouldBe(LoginProvider.CaCertificatePathField);
        refused.Message.ShouldContain(LoginProvider.CaCertificatePathMaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    [Property("Rule", "IDP-6")]
    public void an_update_with_another_issuer_is_refused_and_changes_nothing()
    {
        // Arrange
        var row = Row();

        // Act
        var update = () => row.Update(Form("https://other.example") with { DisplayName = "Renamed" }, null, row.Version);

        // Assert
        var refused = update.ShouldThrow<ValidationException>();
        refused.Property.ShouldBe(LoginProvider.IssuerField);
        refused.Message.ShouldBe(LoginProvider.IssuerIsFixedMessage);
        row.Issuer.ShouldBe("https://idp.example");
        row.DisplayName.ShouldBe("Example");
    }

    [Test]
    [Property("Rule", "IDP-6")]
    public void an_update_with_no_issuer_or_the_same_issuer_keeps_it()
    {
        // Arrange
        var row = Row();

        // Act
        row.Update(Form() with { Issuer = null, DisplayName = "Renamed" }, null, row.Version);
        row.Update(Form() with { DisplayName = "Again" }, null, row.Version);

        // Assert
        row.Issuer.ShouldBe("https://idp.example");
        row.DisplayName.ShouldBe("Again");
    }

    [Test]
    [Property("Rule", "IDP-4")]
    public void an_update_with_no_secret_keeps_the_stored_secret_and_a_new_secret_replaces_it()
    {
        // Arrange
        var row = Row();

        // Act
        row.Update(Form(), null, row.Version);
        var kept = row.ProtectedClientSecret;
        row.Update(Form(), "new protected", row.Version);

        // Assert
        kept.ShouldBe("protected");
        row.ProtectedClientSecret.ShouldBe("new protected");
    }

    [Test]
    public void an_update_from_a_stale_version_is_refused()
    {
        // Arrange
        var row = Row();
        var loaded = row.Version;
        row.Update(Form(), null, loaded);

        // Act
        var update = () => row.Update(Form(), null, loaded);

        // Assert
        update.ShouldThrow<StaleVersionException>();
    }

    [Test]
    [Property("Rule", "IDP-3")]
    [Arguments("example", "https://elsewhere.example")]
    [Arguments("elsewhere", "https://idp.example/")]
    public void an_entry_of_the_environment_with_the_id_or_the_issuer_of_a_row_replaces_it(string id, string issuer)
    {
        // Arrange
        var environment = new[] { new OidcProviderOptions { Id = id, Issuer = issuer } };

        // Act
        var state = Row().StateOf(environment, secretIsReadable: true);

        // Assert
        state.ShouldBe(LoginProviderState.ReplacedByEnvironment);
    }

    [Test]
    [Property("Rule", "IDP-5")]
    public void a_row_whose_secret_the_key_ring_cannot_read_is_secret_unreadable_and_else_usable()
    {
        // Arrange
        var environment = new[] { new OidcProviderOptions { Id = "elsewhere", Issuer = "https://elsewhere.example" } };

        // Act
        var unreadable = Row().StateOf(environment, secretIsReadable: false);
        var readable = Row().StateOf(environment, secretIsReadable: true);

        // Assert
        unreadable.ShouldBe(LoginProviderState.SecretUnreadable);
        readable.ShouldBe(LoginProviderState.Usable);
    }

    [Test]
    [Property("Rule", "IDP-3")]
    public void the_boot_refuses_two_entries_of_the_environment_with_one_issuer()
    {
        // Arrange
        var options = new AuthOptions
        {
            Providers =
            [
                new OidcProviderOptions { Id = "one", Issuer = "https://idp.example", ClientId = "recall", ClientSecret = "secret" },
                new OidcProviderOptions { Id = "two", Issuer = "https://idp.example/", ClientId = "recall", ClientSecret = "secret" },
            ],
        };

        // Act
        var result = new AuthOptionsValidator().Validate(null, options);

        // Assert
        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldContain("Auth:Providers:1:Issuer repeats the issuer of another provider");
    }

    [Test]
    [Property("Rule", "IDP-2")]
    [Arguments("https://idp.example?")]
    [Arguments("https://idp.example/?x=1")]
    [Arguments("https://idp.example#part")]
    public void an_issuer_with_a_query_or_a_fragment_is_refused(string issuer)
    {
        // Act
        var add = () => LoginProvider.Add(Form(issuer), "example", "protected", DateTimeOffset.UnixEpoch);

        // Assert
        var refused = add.ShouldThrow<ValidationException>();
        refused.Property.ShouldBe(LoginProvider.IssuerField);
        refused.Message.ShouldBe(LoginProvider.IssuerHasQueryMessage);
    }

    [Test]
    [Property("Rule", "IDP-3")]
    public void an_issuer_is_stored_with_no_trailing_slash_and_an_update_with_the_slash_keeps_it()
    {
        // Arrange
        var row = Row(issuer: "https://idp.example/");

        // Act
        row.Update(Form("https://idp.example/"), null, row.Version);

        // Assert
        row.Issuer.ShouldBe("https://idp.example");
    }

    [Test]
    [Property("Rule", "IDP-2")]
    public void a_ca_file_over_the_size_limit_is_refused_before_it_is_parsed()
    {
        // Arrange
        var path = Path.GetTempFileName();
        File.WriteAllText(path, new string('a', CaCertificateFile.MaxBytes + 1));

        // Act
        var add = () => LoginProvider.CaCertificatePathOf(path);

        // Assert
        var refused = add.ShouldThrow<ValidationException>();
        File.Delete(path);
        refused.Message.ShouldBe(LoginProvider.UnreadableCertificateMessage);
    }

    [Test]
    [Property("Rule", "IDP-2")]
    [Arguments("empty")]
    [Arguments("/dev/zero")]
    [Arguments("folder")]
    public void an_empty_file_a_device_or_a_folder_is_not_a_ca_file(string kind)
    {
        // Arrange
        var path = kind switch
        {
            "empty" => Path.GetTempFileName(),
            "folder" => Directory.CreateTempSubdirectory("recall-ca").FullName,
            _ => kind,
        };

        // Act
        var certificates = CaCertificateFile.Read(path);

        // Assert
        if (kind == "empty")
            File.Delete(path);
        else if (kind == "folder")
            Directory.Delete(path);
        certificates.ShouldBeNull();
    }

    [Test]
    [Arguments("127.0.0.1", true)]
    [Arguments("127.8.9.10", true)]
    [Arguments("::1", true)]
    [Arguments("::ffff:127.0.0.1", true)]
    [Arguments("169.254.169.254", true)]
    [Arguments("fe80::1", true)]
    [Arguments("0.0.0.0", true)]
    [Arguments("::", true)]
    [Arguments("10.0.0.5", false)]
    [Arguments("192.168.1.20", false)]
    [Arguments("172.16.0.1", false)]
    [Arguments("fd00::5", false)]
    [Arguments("93.184.216.34", false)]
    public void a_provider_from_the_app_refuses_loopback_link_local_and_unspecified_addresses_and_takes_private_ranges(string address, bool refused)
    {
        // Act
        var isRefused = OidcLoginMode.IsRefusedAddress(System.Net.IPAddress.Parse(address));

        // Assert
        isRefused.ShouldBe(refused);
    }
}
