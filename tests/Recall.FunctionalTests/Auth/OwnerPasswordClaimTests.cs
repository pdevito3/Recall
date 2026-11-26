namespace Recall.FunctionalTests.Auth;

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Auth;
using Shouldly;

[NotInParallel("owner-claim-file")]
public class OwnerPasswordClaimTests
{
    private static readonly TokenGrant NoPermissions = new([], JournalAccess.None);

    private static AuthFlowFactory Host => AuthHosts.PasswordClaim;

    private static PasswordPerson Owner => AuthHosts.ClaimOwner;

    private static string ClaimFile => Host.Services.GetRequiredService<OwnerClaim>().FilePath;

    private static string NewPassword() => $"the owner passphrase {Guid.NewGuid():N}";

    private static Task<HttpResponseMessage> PostClaimAsync(AuthBrowser browser, string state, string loginName, string password)
        => browser.PostAsync(AuthRoutes.PasswordClaimOwner, [("state", state), ("loginName", loginName), ("password", password)]);

    private static async Task<string> LoginPageOf(AuthFlowFactory host)
    {
        using var browser = new AuthBrowser(host);
        return await (await browser.GetAsync(AuthRoutes.Login)).Content.ReadAsStringAsync();
    }

    // Each test of this class claims the same Owner, so a test counts only the actions that it adds.
    private static Task<int> ClaimActionsOfOwnerAsync()
        => Host.WithDatabaseAsync(db => db.AuditEntries.IgnoreQueryFilters().CountAsync(e => e.RootId == Owner.UserId && e.Action == "owner_password_set"));

    private static void RemoveEntry(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path);
        else
            File.Delete(path);
    }

    [Test]
    [Property("Rule", "PWD-19")]
    [Arguments("no file")]
    [Arguments("stale file")]
    [Arguments("file from the future")]
    [Arguments("link to a new file")]
    [Arguments("folder")]
    public async Task with_no_fresh_regular_file_the_login_page_offers_nothing_and_the_claim_page_redirects_to_it(string kind)
    {
        // Arrange
        var outside = Directory.CreateTempSubdirectory("recall-claim-target-").FullName;
        var target = Path.Combine(outside, "target");
        var before = await LoginPageOf(Host);
        switch (kind)
        {
            case "stale file":
                await File.WriteAllTextAsync(ClaimFile, "");
                File.SetLastWriteTimeUtc(ClaimFile, DateTime.UtcNow - TimeSpan.FromDays(40));
                break;
            case "file from the future":
                await File.WriteAllTextAsync(ClaimFile, "");
                File.SetLastWriteTimeUtc(ClaimFile, DateTime.UtcNow + TimeSpan.FromHours(1));
                break;
            case "link to a new file":
                await File.WriteAllTextAsync(target, "");
                File.CreateSymbolicLink(ClaimFile, target);
                break;
            case "folder":
                Directory.CreateDirectory(ClaimFile);
                break;
        }

        try
        {
            using var browser = new AuthBrowser(Host);

            // Act
            var login = await browser.GetAsync(AuthRoutes.Login);
            var claimPage = await browser.GetAsync(AuthRoutes.PasswordClaimOwner);

            // Assert
            var page = await login.Content.ReadAsStringAsync();
            page.ShouldNotContain(AuthPages.OwnerClaimLabel);
            page.ShouldNotContain(AuthRoutes.PasswordClaimOwner);
            page.Length.ShouldBe(before.Length);
            claimPage.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            claimPage.Headers.Location!.OriginalString.ShouldBe(AuthRoutes.Login);
            (kind == "no file" || File.Exists(ClaimFile) || Directory.Exists(ClaimFile)).ShouldBeTrue();
        }
        finally
        {
            RemoveEntry(ClaimFile);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Test]
    [Property("Rule", "PWD-19")]
    public async Task a_fresh_file_lets_the_operator_set_the_owner_password_once_and_sign_in()
    {
        // Arrange
        var newer = await PasswordPeople.AddAsync(Host);
        await Host.WithDatabaseAsync(async db =>
        {
            var user = await db.Users.Include(u => u.Roles).Include(u => u.UserPermissions).SingleAsync(u => u.Id == Owner.UserId);
            db.Track(user.RemovePermission(Permission.ManageTenant()));
            db.Track(user.RemovePermission(Permission.ReadAuditLog()));
            var credential = await db.Set<PasswordCredential>().SingleAsync(c => c.UserId == Owner.UserId);
            for (var i = 0; i < PasswordCredential.StopAt; i++)
                credential.NoteFailure();
            return await db.SaveChangesAsync();
        });
        var token = await PatPeople.StartTokenAsync(Host, new PatPerson("", Owner.UserId, ""), NoPermissions);
        var claimsBefore = await ClaimActionsOfOwnerAsync();
        var loginName = $"owner-{Guid.NewGuid():N}";
        var password = NewPassword();
        await File.WriteAllTextAsync(ClaimFile, "");
        using var browser = new AuthBrowser(Host);
        var login = await (await browser.GetAsync(AuthRoutes.Login)).Content.ReadAsStringAsync();
        var state = await browser.FormStateAsync(AuthRoutes.PasswordClaimOwner);

        // Act
        var response = await PostClaimAsync(browser, state, loginName.ToUpperInvariant(), password);
        var again = await new AuthBrowser(Host).GetAsync(AuthRoutes.PasswordClaimOwner);

        // Assert
        login.ShouldContain(AuthPages.OwnerClaimLabel);
        login.ShouldContain($"href=\"{AuthRoutes.PasswordClaimOwner}\"");
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/");
        (await browser.UserOfSessionAsync()).ShouldBe(Owner.UserId);
        File.Exists(ClaimFile).ShouldBeFalse();
        again.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var credential = await PasswordPeople.CredentialOfAsync(Host, Owner.UserId);
        credential.State.ShouldBe(PasswordState.Chosen);
        credential.LoginName.Value.ShouldBe(loginName);
        var held = await Host.WithDatabaseAsync(async db =>
            (await db.Users.Include(u => u.Roles).Include(u => u.UserPermissions).SingleAsync(u => u.Id == Owner.UserId)).Permissions());
        held.ShouldBe(Permission.GetAll(), ignoreOrder: true);
        (await Host.WithDatabaseAsync(db => db.UserPermissions.CountAsync(p => p.UserId == Owner.UserId))).ShouldBe(0);
        (await PatPeople.RowAsync(Host, token.Token.Id)).RevokedReason.ShouldBe(RevokeReason.OwnerClaim);
        (await ClaimActionsOfOwnerAsync()).ShouldBe(claimsBefore + 1);
        (await PasswordPeople.CredentialOfAsync(Host, newer.UserId)).LoginName.Value.ShouldBe(newer.LoginName);
        using var later = new AuthBrowser(Host);
        (await later.SignInAsync(loginName, password)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Test]
    [Property("Rule", "PWD-19")]
    public async Task a_refused_form_keeps_the_file_and_a_post_after_the_file_went_stale_changes_nothing()
    {
        // Arrange
        var before = await PasswordPeople.CredentialOfAsync(Host, Owner.UserId);
        var taken = await PasswordPeople.AddAsync(Host);
        await File.WriteAllTextAsync(ClaimFile, "");
        try
        {
            using var browser = new AuthBrowser(Host);
            var tooShort = await PostClaimAsync(browser, await browser.FormStateAsync(AuthRoutes.PasswordClaimOwner), "owner", "short");
            var nameInUse = await PostClaimAsync(browser, PasswordPeople.StateIn(await tooShort.Content.ReadAsStringAsync()), taken.LoginName, NewPassword());
            var fileAfterRefusals = File.Exists(ClaimFile);
            var state = PasswordPeople.StateIn(await nameInUse.Content.ReadAsStringAsync());
            File.SetLastWriteTimeUtc(ClaimFile, DateTime.UtcNow - TimeSpan.FromDays(40));

            // Act
            var stale = await PostClaimAsync(browser, state, $"owner-{Guid.NewGuid():N}", NewPassword());

            // Assert
            tooShort.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await tooShort.Content.ReadAsStringAsync()).ShouldContain(PasswordPolicy.TooShortMessage);
            nameInUse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await nameInUse.Content.ReadAsStringAsync()).ShouldContain(LoginName.InUseMessage);
            fileAfterRefusals.ShouldBeTrue();
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var page = await stale.Content.ReadAsStringAsync();
            page.ShouldContain(PasswordLogin.ClaimGoneMessage);
            page.ShouldNotContain(AuthPages.OwnerClaimLabel);
            browser.SessionCookie.ShouldBeNull();
            File.Exists(ClaimFile).ShouldBeTrue();
            var after = await PasswordPeople.CredentialOfAsync(Host, Owner.UserId);
            after.PasswordHash.ShouldBe(before.PasswordHash);
            after.LoginName.ShouldBe(before.LoginName);
        }
        finally
        {
            File.Delete(ClaimFile);
        }
    }

    [Test]
    [Property("Rule", "PWD-19")]
    public async Task with_no_fresh_file_a_post_gives_the_same_answer_for_a_held_and_a_free_login_name_and_hashes_nothing()
    {
        // Arrange
        var held = await PasswordPeople.AddAsync(Host);
        var heldPassword = NewPassword();
        var freePassword = NewPassword();
        using var browser = new AuthBrowser(Host);

        // Act
        var withHeld = await PostClaimAsync(browser, await browser.FormStateAsync(AuthRoutes.Login), held.LoginName, heldPassword);
        var withFree = await PostClaimAsync(browser, await browser.FormStateAsync(AuthRoutes.Login), $"free-{Guid.NewGuid():N}", freePassword);

        // Assert
        foreach (var response in new[] { withHeld, withFree })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var page = await response.Content.ReadAsStringAsync();
            page.ShouldContain(PasswordLogin.ClaimGoneMessage);
            page.ShouldNotContain(LoginName.InUseMessage);
        }

        (Host.Hasher.HashesOf(heldPassword), Host.Hasher.HashesOf(freePassword)).ShouldBe((0, 0));
        browser.SessionCookie.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "PWD-19")]
    public async Task a_claim_post_with_no_attempt_or_another_browsers_attempt_gets_the_refused_page_and_keeps_the_file()
    {
        // Arrange
        await File.WriteAllTextAsync(ClaimFile, "");
        try
        {
            using var browser = new AuthBrowser(Host);
            using var other = new AuthBrowser(Host);
            var state = await browser.FormStateAsync(AuthRoutes.PasswordClaimOwner);

            // Act
            var noAttempt = await PostClaimAsync(browser, "no-such-state", "owner", NewPassword());
            var otherBrowser = await PostClaimAsync(other, state, "owner", NewPassword());

            // Assert
            foreach (var refused in new[] { noAttempt, otherBrowser })
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                (await refused.Content.ReadAsStringAsync()).ShouldContain("This sign-in is not valid");
            }

            File.Exists(ClaimFile).ShouldBeTrue();
            other.SessionCookie.ShouldBeNull();
        }
        finally
        {
            File.Delete(ClaimFile);
        }
    }

    [Test]
    [Property("Rule", "PWD-19")]
    public async Task with_a_fresh_file_a_single_provider_shows_the_offer_and_its_button_and_the_oidc_claim_still_works()
    {
        // Arrange
        var host = AuthHosts.Sweep;
        var file = host.Services.GetRequiredService<OwnerClaim>().FilePath;
        using (var first = new AuthBrowser(host))
            await first.LoginWebAsync($"first-{Guid.NewGuid():N}");
        var before = await new AuthBrowser(host).GetAsync(AuthRoutes.Login);
        await File.WriteAllTextAsync(file, "");
        try
        {
            using var browser = new AuthBrowser(host);

            // Act
            var login = await browser.GetAsync(AuthRoutes.Login);
            var claimer = $"claimer-{Guid.NewGuid():N}";
            await browser.LoginWebAsync(claimer);

            // Assert
            before.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            login.StatusCode.ShouldBe(HttpStatusCode.OK);
            var page = await login.Content.ReadAsStringAsync();
            page.ShouldContain(AuthPages.OwnerClaimLabel);
            page.ShouldContain("Sign in with Stub");
            File.Exists(file).ShouldBeFalse();
            browser.SessionCookie.ShouldNotBeNull();
            var roles = await host.WithDatabaseAsync(db => db.UserRoles
                .Where(r => db.Set<ExternalLogin>().Any(link => link.Subject == claimer && link.UserId == r.UserId))
                .Select(r => r.Role.Value)
                .ToListAsync());
            roles.ShouldBe([UserRole.Admin().Value]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    [Property("Rule", "PWD-19")]
    public async Task an_issue_between_the_claim_and_its_session_keeps_the_temporary_password_and_starts_no_session()
    {
        // Arrange
        var admin = await PasswordPeople.AddAsync(Host, sameTenantAs: Owner);
        using var adminBrowser = new AuthBrowser(Host);
        (await adminBrowser.SignInAsync(admin.LoginName, admin.Password)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await File.WriteAllTextAsync(ClaimFile, "");
        using var browser = new AuthBrowser(Host);
        var state = await browser.FormStateAsync(AuthRoutes.PasswordClaimOwner);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passwords = Host.Services.GetRequiredService<PasswordLogin>();
        passwords.BeforeSessionWrite = async userId =>
        {
            if (userId != Owner.UserId)
                return;
            reached.TrySetResult();
            await released.Task;
        };

        // Act
        HttpResponseMessage response;
        HttpResponseMessage issue;
        try
        {
            var claim = PostClaimAsync(browser, state, $"owner-{Guid.NewGuid():N}", NewPassword());
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            issue = await adminBrowser.IssueTemporaryAsync(admin, Owner.UserId);
            released.TrySetResult();
            response = await claim;
        }
        finally
        {
            released.TrySetResult();
            passwords.BeforeSessionWrite = null;
            File.Delete(ClaimFile);
        }

        // Assert
        issue.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("This sign-in is not valid");
        browser.SessionCookie.ShouldBeNull();
        (await PasswordPeople.CredentialOfAsync(Host, Owner.UserId)).State.ShouldBe(PasswordState.Temporary);
        (await Host.WithDatabaseAsync(db => db.Set<AuthSession>().CountAsync(s => s.UserId == Owner.UserId && s.RevokedAt == null))).ShouldBe(0);
    }
}
