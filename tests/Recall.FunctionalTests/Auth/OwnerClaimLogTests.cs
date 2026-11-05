namespace Recall.FunctionalTests.Auth;

using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Auth;
using Serilog.Events;
using Shouldly;

public class OwnerClaimLogTests
{
    [Test]
    public async Task a_claim_file_that_is_too_old_gets_a_warning_that_names_the_file()
    {
        // Arrange
        var host = AuthHosts.Sweep;
        var file = host.Services.GetRequiredService<OwnerClaim>().FilePath;
        await File.WriteAllTextAsync(file, "");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - TimeSpan.FromDays(40));
        using var browser = new AuthBrowser(host);

        // Act
        await browser.LoginWebAsync($"stale-claim-{Guid.NewGuid():N}");

        // Assert
        File.Exists(file).ShouldBeFalse();
        host.Logs.Holding(file).ShouldContain(line => line.Level == LogEventLevel.Warning && line.MessageTemplate.Text.Contains("did not use it", StringComparison.Ordinal));
    }
}
