namespace Recall.IntegrationTests.FeatureTests.Users;

using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Domain.AuditEntries;
using Recall.Server.Domain.PersonalAccessTokens;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;
using Recall.Server.Exceptions;
using Recall.Server.Resources.Entrypoints.Auth;
using Recall.SharedTestHelpers.EntityGraphs;
using Shouldly;

public class IssueTemporaryPasswordCommandTests : TestBase
{
    [Test]
    [Property("Rule", "PWD-14")]
    public async Task the_first_issue_sets_the_login_name_and_stores_only_a_hash_of_four_words()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUsers(out var users, u => u.Role = "Admin", u => u.Role = "User")
            .BuildAsync();
        var (admin, person) = (users[0], users[1]);
        SignInAsPerson(scope, admin);

        // Act
        var issued = await scope.SendAsync(new IssueTemporaryPassword.Command(person.Id, $"Ada.Lovelace-{person.Id}"));

        // Assert
        issued.LoginName.ShouldBe($"ada.lovelace-{person.Id}".ToLowerInvariant());
        issued.Password.ShouldMatch("^[a-z]+(-[a-z]+){3,7}$");
        var credential = await CredentialAsync(scope, person.Id);
        credential.State.ShouldBe(PasswordState.Temporary);
        credential.LoginName.Value.ShouldBe(issued.LoginName);
        credential.TemporaryUntil.ShouldBe(credential.ChangedAt + PasswordCredential.TemporaryLifetime);
        // The column keeps the instant to the millisecond.
        issued.ExpiresAt.ShouldBe(credential.TemporaryUntil!.Value, TimeSpan.FromMilliseconds(1));
        credential.PasswordHash.ShouldNotContain(issued.Password);
        new PasswordHasher<PasswordCredential>().VerifyHashedPassword(credential, credential.PasswordHash, issued.Password)
            .ShouldBe(PasswordVerificationResult.Success);
        // The format of PasswordHasher v3: a marker byte, the PRF, then the iteration count, each big-endian.
        BinaryPrimitives.ReadUInt32BigEndian(Convert.FromBase64String(credential.PasswordHash).AsSpan(5, 4)).ShouldBe(220_000u);
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public async Task an_issue_ends_each_session_and_each_token_of_the_user_and_no_session_of_another()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUsers(out var users, u => u.Role = "Admin", u => u.Role = "User")
            .BuildAsync();
        var (admin, person) = (users[0], users[1]);
        await StartAsync(scope, store => store.StartWebAsync(person.Id, "oidc", CancellationToken.None));
        await StartAsync(scope, store => store.StartNativeAsync(person.Id, "oidc", CancellationToken.None));
        await StartAsync(scope, store => store.StartPersonalAccessTokenAsync(
            person, PersonalAccessTokenName.Of("Laptop"), new TokenGrant([], JournalAccess.None), null, CancellationToken.None));
        var adminSession = await StartAsync(scope, store => store.StartWebAsync(admin.Id, "oidc", CancellationToken.None));
        SignInAsPerson(scope, admin);

        // Act
        await scope.SendAsync(new IssueTemporaryPassword.Command(person.Id, $"ada-{person.Id}"));

        // Assert
        var sessions = await scope.ExecuteDbContextAsync(db => db.Set<AuthSession>().AsNoTracking()
            .Where(s => s.UserId == person.Id || s.UserId == admin.Id).ToListAsync());
        var ofPerson = sessions.Where(s => s.UserId == person.Id).ToList();
        ofPerson.Count.ShouldBe(3);
        ofPerson.ShouldAllBe(s => s.RevokedAt != null && s.RevokedReason == RevokeReason.TemporaryPassword);
        ofPerson.Select(s => s.ClientKind).ShouldBe([SessionKind.Web, SessionKind.Native, SessionKind.Pat], ignoreOrder: true);
        sessions.Single(s => s.Id == adminSession.SessionId).RevokedAt.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public async Task an_issue_to_the_caller_keeps_the_session_of_the_request_and_ends_the_others()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUser(out var admin, u => u.Role = "Admin")
            .BuildAsync();
        var current = await StartAsync(scope, store => store.StartWebAsync(admin.Id, "oidc", CancellationToken.None));
        var otherWeb = await StartAsync(scope, store => store.StartWebAsync(admin.Id, "oidc", CancellationToken.None));
        await StartAsync(scope, store => store.StartNativeAsync(admin.Id, "oidc", CancellationToken.None));
        SignInAsPerson(scope, admin, current.SessionId);

        // Act
        await scope.SendAsync(new IssueTemporaryPassword.Command(admin.Id, $"me-{admin.Id}"));

        // Assert
        var sessions = await scope.ExecuteDbContextAsync(db => db.Set<AuthSession>().AsNoTracking().Where(s => s.UserId == admin.Id).ToListAsync());
        sessions.Count.ShouldBe(3);
        sessions.Single(s => s.Id == current.SessionId).RevokedAt.ShouldBeNull();
        sessions.Where(s => s.Id != current.SessionId).ShouldAllBe(s => s.RevokedAt != null && s.RevokedReason == RevokeReason.TemporaryPassword);
        sessions.Single(s => s.Id == otherWeb.SessionId).RevokedAt.ShouldNotBeNull();
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public async Task an_issue_deletes_each_login_attempt_and_app_code_of_the_user_and_none_of_another()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUsers(out var users, u => u.Role = "Admin", u => u.Role = "User")
            .BuildAsync();
        var (admin, person) = (users[0], users[1]);
        await StartAsync(scope, async store =>
        {
            await store.AddAttemptAsync(ChooseAttempt(person.Id), CancellationToken.None);
            await store.AddAttemptAsync(ChooseAttempt(admin.Id), CancellationToken.None);
            return await store.AddAppCodeAsync(person.Id, "password", "recall://auth/callback", "challenge", null, CancellationToken.None);
        });
        await StartAsync(scope, store => store.AddAppCodeAsync(admin.Id, "password", "recall://auth/callback", "challenge", null, CancellationToken.None));
        SignInAsPerson(scope, admin);

        // Act
        await scope.SendAsync(new IssueTemporaryPassword.Command(person.Id, $"ada-{person.Id}"));

        // Assert
        var attempts = await scope.ExecuteDbContextAsync(db => db.Set<LoginAttempt>().AsNoTracking()
            .Where(a => a.UserId == person.Id || a.UserId == admin.Id).Select(a => a.UserId).ToListAsync());
        attempts.ShouldBe([admin.Id]);
        var codes = await scope.ExecuteDbContextAsync(db => db.Set<AppCode>().AsNoTracking()
            .Where(c => c.UserId == person.Id || c.UserId == admin.Id).Select(c => c.UserId).ToListAsync());
        codes.ShouldBe([admin.Id]);
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public async Task an_issue_records_the_audit_action_with_no_password()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUsers(out var users, u => u.Role = "Admin", u => u.Role = "User")
            .BuildAsync();
        var (admin, person) = (users[0], users[1]);
        SignInAsPerson(scope, admin);

        // Act
        var issued = await scope.SendAsync(new IssueTemporaryPassword.Command(person.Id, $"ada-{person.Id}"));

        // Assert
        await using var system = TestFixture.BaseScopeFactory.CreateSystemScope();
        var entry = await system.ServiceProvider.GetRequiredService<AppDbContext>().AuditEntries.AsNoTracking()
            .SingleAsync(e => e.RootId == person.Id && e.Kind == AuditEntryKind.Action);
        (entry.Action, entry.EntityType, entry.EntityId).ShouldBe(("temporary_password_issued", "User", person.Id));
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(entry.Data)!;
        data["LoginName"].GetString().ShouldBe(issued.LoginName);
        data["ExpiresAt"].GetDateTimeOffset().ShouldBe(issued.ExpiresAt);
        entry.Data.ShouldNotContain(issued.Password);
        var hash = (await CredentialAsync(scope, person.Id)).PasswordHash;
        entry.Data.ShouldNotContain(hash);
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public async Task a_later_issue_keeps_the_login_name_and_ends_a_stop()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUsers(out var users, u => u.Role = "Admin", u => u.Role = "User")
            .BuildAsync();
        var (admin, person) = (users[0], users[1]);
        await scope.AddPasswordAsync(person, stopped: true);
        var before = await CredentialAsync(scope, person.Id);
        SignInAsPerson(scope, admin);

        // Act
        var issued = await scope.SendAsync(new IssueTemporaryPassword.Command(person.Id, "another-name"));

        // Assert
        issued.LoginName.ShouldBe(before.LoginName.Value);
        var after = await CredentialAsync(scope, person.Id);
        (after.State, after.LoginName, after.FailedCount).ShouldBe((PasswordState.Temporary, before.LoginName, 0));
        after.PasswordHash.ShouldNotBe(before.PasswordHash);
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public async Task an_admin_can_issue_to_a_user_with_a_sign_in_link_and_to_themselves()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUsers(out var users, u => u.Role = "Admin", u => u.Role = "User")
            .BuildAsync();
        var (admin, linked) = (users[0], users[1]);
        await scope.AddSignInLinkAsync(linked);
        SignInAsPerson(scope, admin);

        // Act
        var toLinked = await scope.SendAsync(new IssueTemporaryPassword.Command(linked.Id, $"linked-{linked.Id}"));
        var toSelf = await scope.SendAsync(new IssueTemporaryPassword.Command(admin.Id, $"me-{admin.Id}"));

        // Assert
        (toLinked.LoginName, toSelf.LoginName).ShouldBe(($"linked-{linked.Id}".ToLowerInvariant(), $"me-{admin.Id}".ToLowerInvariant()));
        (await CredentialAsync(scope, admin.Id)).State.ShouldBe(PasswordState.Temporary);
    }

    [Test]
    [Property("Rule", "PWD-14")]
    [Arguments(null, IssueTemporaryPassword.LoginNameNeededMessage)]
    [Arguments(" ", IssueTemporaryPassword.LoginNameNeededMessage)]
    [Arguments("ada lovelace", LoginName.NotValidMessage)]
    public async Task a_first_issue_needs_a_valid_login_name(string? loginName, string message)
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUsers(out var users, u => u.Role = "Admin", u => u.Role = "User")
            .BuildAsync();
        var (admin, person) = (users[0], users[1]);
        SignInAsPerson(scope, admin);

        // Act
        var act = () => scope.SendAsync(new IssueTemporaryPassword.Command(person.Id, loginName));

        // Assert
        var refused = await act.ShouldThrowAsync<ValidationException>();
        (refused.Property, refused.Message).ShouldBe(("loginName", message));
        (await scope.ExecuteDbContextAsync(db => db.Set<PasswordCredential>().CountAsync(c => c.UserId == person.Id))).ShouldBe(0);
    }

    [Test]
    [Property("Rule", "PWD-14")]
    [Property("Rule", "PWD-18")]
    public async Task a_login_name_in_use_in_another_tenant_gets_409_and_ends_no_session()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUsers(out var users, u => u.Role = "Admin", u => u.Role = "User")
            .BuildAsync();
        await scope.StartGraph(out _).AddTenant(out _).AddUser(out var stranger).BuildAsync();
        var (admin, person) = (users[0], users[1]);
        await scope.AddPasswordAsync(stranger);
        var taken = (await CredentialAsync(scope, stranger.Id)).LoginName.Value;
        var session = await StartAsync(scope, store => store.StartWebAsync(person.Id, "oidc", CancellationToken.None));
        SignInAsPerson(scope, admin);

        // Act
        var act = () => scope.SendAsync(new IssueTemporaryPassword.Command(person.Id, taken.ToUpperInvariant()));

        // Assert
        var refused = await act.ShouldThrowAsync<ConflictException>();
        refused.Message.ShouldBe(LoginName.InUseMessage);
        (await scope.ExecuteDbContextAsync(db => db.Set<AuthSession>().AsNoTracking().SingleAsync(s => s.Id == session.SessionId)))
            .RevokedAt.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public async Task a_caller_who_lacks_a_permission_of_the_user_gets_403()
    {
        // Arrange
        var scope = new TestingServiceScope();
        var graph = scope.StartGraph(out _).AddUsers(out var users, u => u.Role = "User", u => u.Role = "Admin");
        var (manager, admin) = (users[0], users[1]);
        manager.AddPermissions([Permission.ManageUserAccess(), Permission.ReadUsers()]);
        await graph.BuildAsync();
        SignInAsPerson(scope, manager);

        // Act
        var act = () => scope.SendAsync(new IssueTemporaryPassword.Command(admin.Id, $"admin-{admin.Id}"));

        // Assert
        await act.ShouldThrowAsync<ForbiddenAccessException>();
        (await scope.ExecuteDbContextAsync(db => db.Set<PasswordCredential>().CountAsync(c => c.UserId == admin.Id))).ShouldBe(0);
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public async Task a_user_of_another_tenant_is_not_found()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _).AddUser(out var admin, u => u.Role = "Admin").BuildAsync();
        await scope.StartGraph(out _).AddTenant(out _).AddUser(out var stranger).BuildAsync();
        SignInAsPerson(scope, admin);

        // Act
        var act = () => scope.SendAsync(new IssueTemporaryPassword.Command(stranger.Id, $"stranger-{stranger.Id}"));

        // Assert
        await act.ShouldThrowAsync<NotFoundException>();
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public async Task a_provider_token_and_an_agent_get_403_even_with_the_permission()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUsers(out var users, u => u.Role = "Admin", u => u.Role = "User")
            .BuildAsync();
        var (admin, person) = (users[0], users[1]);

        // Act
        scope.SetUserWithDefaults(identifier: admin.Identifier);
        var asProviderToken = () => scope.SendAsync(new IssueTemporaryPassword.Command(person.Id, $"ada-{person.Id}"));
        var providerRefusal = await asProviderToken.ShouldThrowAsync<ForbiddenAccessException>();
        scope.SetDelegatedClient(admin.Identifier, "clinic-assistant");
        var asAgent = () => scope.SendAsync(new IssueTemporaryPassword.Command(person.Id, $"ada-{person.Id}"));
        var agentRefusal = await asAgent.ShouldThrowAsync<ForbiddenAccessException>();

        // Assert
        providerRefusal.Message.ShouldBe("Only a person signed in to Recall can do this.");
        agentRefusal.Message.ShouldBe("Only a person signed in to Recall can do this.");
        (await scope.ExecuteDbContextAsync(db => db.Set<PasswordCredential>().CountAsync(c => c.UserId == person.Id))).ShouldBe(0);
    }

    [Test]
    [Property("Rule", "PWD-18")]
    public async Task the_user_record_shows_the_login_name_after_the_first_issue()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddUsers(out var users, u => u.Role = "Admin", u => u.Role = "User")
            .BuildAsync();
        var (admin, person) = (users[0], users[1]);
        SignInAsPerson(scope, admin);
        var before = await scope.SendAsync(new GetUser.Query(person.Id));

        // Act
        await scope.SendAsync(new IssueTemporaryPassword.Command(person.Id, $"Ada-{person.Id}"));
        var after = await scope.SendAsync(new GetUser.Query(person.Id));

        // Assert
        before.LoginName.ShouldBeNull();
        after.LoginName.ShouldBe($"ada-{person.Id}".ToLowerInvariant());
        after.AllowedActions.ShouldContain(UserAction.IssueTemporaryPassword().Value);
    }

    private static void SignInAsPerson(TestingServiceScope scope, User user, string? sessionId = null)
    {
        scope.SetDelegatedClient(user.Identifier, FirstPartyClientOptions.AppClientId);
        scope.GetService<ICurrentUserService>().GetClaimValue(AppSessionHandler.SessionIdClaim).Returns(sessionId);
    }

    private static LoginAttempt ChooseAttempt(string userId) => new()
    {
        StateHash = TokenHash.Of(Secrets.New()).Hex,
        BindingHash = TokenHash.Of(Secrets.New()).Hex,
        ModeId = PasswordLoginMode.ModeId,
        Nonce = "",
        PkceVerifier = "",
        RedirectUri = "",
        ClientKind = SessionKind.Web,
        UserId = userId,
        CredentialChangedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
    };

    private static Task<T> StartAsync<T>(TestingServiceScope scope, Func<SessionStore, Task<T>> start)
        => scope.ExecuteScopeAsync(sp => start(sp.GetRequiredService<SessionStore>()));

    private static Task<PasswordCredential> CredentialAsync(TestingServiceScope scope, string userId)
        => scope.ExecuteDbContextAsync(db => db.Set<PasswordCredential>().AsNoTracking().SingleAsync(c => c.UserId == userId));
}
