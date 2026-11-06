namespace Recall.UnitTests.Domain.Support;

using Recall.Server.Domain.Support;
using Shouldly;

public class RedactedSettingsTests
{
    private static KeyValuePair<string, string?> Setting(string key, string? value) => new(key, value);

    [Test]
    public void a_setting_shows_its_value_only_when_it_is_on_the_list()
    {
        // Arrange
        KeyValuePair<string, string?>[] settings =
        [
            Setting("Auth:Providers:0:ClientId", "recall"),
            Setting("Auth:Providers:0:ClientSecret", "the-secret"),
            Setting("auth:providers:1:issuer", "https://idp.example"),
            Setting("ConnectionStrings:appdb", "Data Source=/data/recall.db;Password=the-secret"),
            Setting("Auth:ProviderApiKey", "the-secret"),
            Setting("FileTelemetry:Logs:FileSizeBytes", "1024"),
            Setting("Serilog:MinimumLevel:Default", "Information"),
            Setting("Serilog:WriteTo:0:Args:apiKey", "the-secret"),
            Setting("Kestrel:Endpoints:http:Url", "http://+:8080"),
            Setting("Kestrel:Endpoints:http:Certificate:Password", "the-secret"),
            Setting("SOME_TOKEN", "the-secret"),
        ];

        // Act
        var shown = RedactedSettings.Of(settings);

        // Assert
        shown["Auth:Providers:0:ClientId"].ShouldBe("recall");
        shown["auth:providers:1:issuer"].ShouldBe("https://idp.example");
        shown["FileTelemetry:Logs:FileSizeBytes"].ShouldBe("1024");
        shown["Serilog:MinimumLevel:Default"].ShouldBe("Information");
        shown["Kestrel:Endpoints:http:Url"].ShouldBe("http://+:8080");
        shown["Auth:Providers:0:ClientSecret"].ShouldBe(RedactedSettings.Hidden);
        shown["ConnectionStrings:appdb"].ShouldBe(RedactedSettings.Hidden);
        shown["Auth:ProviderApiKey"].ShouldBe(RedactedSettings.Hidden);
        shown["Serilog:WriteTo:0:Args:apiKey"].ShouldBe(RedactedSettings.Hidden);
        shown["Kestrel:Endpoints:http:Certificate:Password"].ShouldBe(RedactedSettings.Hidden);
        shown["SOME_TOKEN"].ShouldBe(RedactedSettings.Hidden);
        shown.Values.ShouldNotContain("the-secret");
    }

    [Test]
    public void a_section_with_no_value_is_not_a_setting()
    {
        // Arrange
        KeyValuePair<string, string?>[] settings = [Setting("Auth", null)];

        // Act
        var shown = RedactedSettings.Of(settings);

        // Assert
        shown.ShouldBeEmpty();
    }
}
