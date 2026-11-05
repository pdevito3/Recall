namespace Recall.FunctionalTests.Auth;

using System.Globalization;
using Serilog.Events;
using Shouldly;

public class LogInjectionTests
{
    [Test]
    public async Task a_path_with_an_encoded_crlf_cannot_forge_a_log_line()
    {
        // Arrange
        var host = AuthHosts.Sweep;
        var marker = $"forged-{Guid.NewGuid():N}";
        using var browser = new AuthBrowser(host);

        // Act
        await browser.GetAsync($"/x%0D%0A%5B12:00:00%20ERR%5D%20{marker}");

        // Assert
        var requestLine = host.Logs.Holding(marker).Single(line => line.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal));
        requestLine.Properties["RequestPath"].ShouldBeOfType<ScalarValue>().Value.ShouldBe($"/x[12:00:00 ERR] {marker}");
        var rendered = requestLine.RenderMessage(CultureInfo.InvariantCulture);
        rendered.ShouldNotContain("\r");
        rendered.ShouldNotContain("\n");
    }
}
