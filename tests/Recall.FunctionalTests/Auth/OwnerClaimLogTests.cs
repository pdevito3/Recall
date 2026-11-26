namespace Recall.FunctionalTests.Auth;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;
using Serilog.Events;
using Shouldly;
using TUnit.Core.Enums;

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
            (await RolesOfAsync(host, strangerSubject)).ShouldBeEmpty();
            host.Logs.Holding(file).ShouldContain(line => line.Level == LogEventLevel.Warning && line.MessageTemplate.Text.Contains("Could not remove", StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(folder, writable);
            File.Delete(file);
        }
    }

    [Test]
    [Arguments("dangling link")]
    [Arguments("link to a new file")]
    [Arguments("folder")]
    public async Task an_entry_that_is_not_a_regular_file_refuses_the_claim_and_each_login_goes_on(string kind)
    {
        // Arrange
        var host = AuthHosts.Sweep;
        var file = host.Services.GetRequiredService<OwnerClaim>().FilePath;
        var outside = Directory.CreateTempSubdirectory("recall-claim-target-").FullName;
        var linkedSubject = $"linked-{Guid.NewGuid():N}";
        var strangerSubject = $"stranger-{Guid.NewGuid():N}";
        using (var first = new AuthBrowser(host))
            await first.LoginWebAsync(linkedSubject);
        var target = Path.Combine(outside, "target");
        switch (kind)
        {
            case "dangling link":
                File.CreateSymbolicLink(file, target);
                break;
            case "link to a new file":
                await File.WriteAllTextAsync(target, "");
                File.CreateSymbolicLink(file, target);
                break;
            default:
                Directory.CreateDirectory(file);
                break;
        }

        var seen = host.Logs.All.Count;
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
            (await RolesOfAsync(host, strangerSubject)).ShouldBeEmpty();
            host.Logs.All.Skip(seen).Count(line => line.Level == LogEventLevel.Warning && line.RenderMessage(CultureInfo.InvariantCulture).Contains(file, StringComparison.Ordinal)).ShouldBe(2);
        }
        finally
        {
            if (Directory.Exists(file))
                Directory.Delete(file);
            else
                File.Delete(file);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Test]
    [RunOn(OS.MacOs)]
    [UnsupportedOSPlatform("windows")]
    public async Task many_logins_with_an_immutable_claim_file_add_no_entry_to_its_folder()
    {
        // Arrange
        var host = AuthHosts.Sweep;
        var file = host.Services.GetRequiredService<OwnerClaim>().FilePath;
        var folder = Path.GetDirectoryName(file)!;
        var linkedSubject = $"linked-{Guid.NewGuid():N}";
        using (var first = new AuthBrowser(host))
            await first.LoginWebAsync(linkedSubject);
        await File.WriteAllTextAsync(file, "");
        await ChangeFlagAsync("uchg", file);
        try
        {
            // Act
            for (var login = 0; login < 8; login++)
            {
                using var linked = new AuthBrowser(host);
                await linked.LoginWebAsync(linkedSubject);
                linked.SessionCookie.ShouldNotBeNull();
            }

            // Assert
            Directory.GetFileSystemEntries(folder, OwnerClaim.FileName + "*").ShouldBe([file]);
        }
        finally
        {
            foreach (var entry in Directory.GetFileSystemEntries(folder, OwnerClaim.FileName + "*"))
            {
                await ChangeFlagAsync("nouchg", entry);
                File.Delete(entry);
            }
        }
    }

    [Test]
    public async Task a_claim_that_adds_a_new_admin_logs_a_warning_with_the_new_user_id()
    {
        // Arrange
        var host = AuthHosts.Sweep;
        var file = host.Services.GetRequiredService<OwnerClaim>().FilePath;
        var subject = $"new-admin-{Guid.NewGuid():N}";
        using (var first = new AuthBrowser(host))
            await first.LoginWebAsync($"admin-{Guid.NewGuid():N}");
        var adminRole = UserRole.Admin().Value;
        var moved = await host.WithDatabaseAsync(async db =>
        {
            var admins = await db.Users.Where(u => u.Roles.Any(r => r.Role.Value == adminRole) && u.Identifier == u.Id).ToListAsync();
            admins.ForEach(admin => db.Entry(admin).Property(u => u.Identifier).CurrentValue = $"elsewhere-{admin.Id}");
            await db.SaveChangesAsync();
            return admins.Select(admin => admin.Id).ToList();
        });
        await File.WriteAllTextAsync(file, "");
        try
        {
            using var claimant = new AuthBrowser(host);

            // Act
            await claimant.LoginWebAsync(subject);

            // Assert
            (await RolesOfAsync(host, subject)).ShouldBe([adminRole]);
            var added = await host.WithDatabaseAsync(db => db.Set<ExternalLogin>().Where(link => link.Subject == subject).Select(link => link.UserId).SingleAsync());
            added.ShouldNotBeOneOf(moved.ToArray());
            host.Logs.Holding(added).ShouldContain(line => line.Level == LogEventLevel.Warning);
        }
        finally
        {
            await host.WithDatabaseAsync(async db =>
            {
                var admins = await db.Users.Where(u => moved.Contains(u.Id)).ToListAsync();
                admins.ForEach(admin => db.Entry(admin).Property(u => u.Identifier).CurrentValue = admin.Id);
                return await db.SaveChangesAsync();
            });
        }
    }

    private static Task<List<string>> RolesOfAsync(AuthFlowFactory host, string subject)
        => host.WithDatabaseAsync(db => db.UserRoles
            .Where(r => db.Set<ExternalLogin>().Any(link => link.Subject == subject && link.UserId == r.UserId))
            .Select(r => r.Role.Value)
            .ToListAsync());

    private static async Task ChangeFlagAsync(string flag, string path)
    {
        using var chflags = Process.Start("chflags", [flag, path]);
        await chflags.WaitForExitAsync();
        chflags.ExitCode.ShouldBe(0);
    }
}
