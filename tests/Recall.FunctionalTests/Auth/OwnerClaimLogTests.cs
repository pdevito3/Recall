namespace Recall.FunctionalTests.Auth;

using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;
using Serilog.Events;
using Shouldly;

[NotInParallel("owner-claim-file")]
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

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task a_claim_file_that_cannot_be_renamed_refuses_the_claim_and_each_login_goes_on()
    {
        // Arrange
        var host = AuthHosts.Sweep;
        var file = host.Services.GetRequiredService<OwnerClaim>().FilePath;
        var folder = Path.GetDirectoryName(file)!;
        var linkedSubject = $"linked-{Guid.NewGuid():N}";
        var strangerSubject = $"stranger-{Guid.NewGuid():N}";
        using (var first = new AuthBrowser(host))
            await first.LoginWebAsync(linkedSubject);
        await File.WriteAllTextAsync(file, "");
        var writable = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var linked = new AuthBrowser(host);
            using var stranger = new AuthBrowser(host);

            // Act
            await linked.LoginWebAsync(linkedSubject);
            await stranger.LoginWebAsync(strangerSubject);

            // Assert
            linked.SessionCookie.ShouldNotBeNull();
            stranger.SessionCookie.ShouldNotBeNull();
            File.Exists(file).ShouldBeTrue();
            var role = await host.WithDatabaseAsync(db => db.Users
                .Where(u => db.Set<ExternalLogin>().Any(link => link.Subject == strangerSubject && link.UserId == u.Id))
                .Select(u => u.Role.Value)
                .SingleAsync());
            role.ShouldBe(UserRole.User().Value);
            host.Logs.Holding(file).ShouldContain(line => line.Level == LogEventLevel.Warning && line.MessageTemplate.Text.Contains("Could not remove", StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(folder, writable);
            File.Delete(file);
        }
    }
}
