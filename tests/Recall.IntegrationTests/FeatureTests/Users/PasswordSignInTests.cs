namespace Recall.IntegrationTests.FeatureTests.Users;

using Microsoft.EntityFrameworkCore;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants.Features;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Features;
using Recall.Server.Exceptions;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.IntegrationTests.DomainEvents;
using Shouldly;

/// <summary>A password credential as a way to sign in, and its row next to the user.</summary>
public class PasswordSignInTests : TestBase
{
    [Test]
    [Property("Rule", "PWD-22")]
    public async Task an_admin_with_a_password_and_no_link_counts_as_another_admin_who_can_sign_in()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Roles = ["Admin"])
            .Under(tenant)
            .AddUser(out var other, u => u.Roles = ["Admin"])
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        await scope.AddPasswordAsync(other);
        scope.SetUserWithDefaults(identifier: other.Identifier);

        // Act
        var result = await scope.SendAsync(new RemoveUserRole.Command(owner.Id, "Admin"));

        // Assert
        result.Roles.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "PWD-22")]
    public async Task the_only_admin_with_a_password_cannot_be_demoted_for_an_admin_who_cannot_sign_in()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Roles = ["Admin"])
            .Under(tenant)
            .AddUser(out _, u => u.Roles = ["Admin"])
            .BuildAsync();
        await scope.AddPasswordAsync(owner);
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        var act = () => scope.SendAsync(new DeleteUser.Command(owner.Id));

        // Assert
        (await act.ShouldThrowAsync<ConflictException>()).Message.ShouldBe(LastAdmin.RefusedMessage);
    }

    [Test]
    [Property("Rule", "PWD-22")]
    public async Task an_admin_whose_password_is_stopped_does_not_count_as_another_admin()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Roles = ["Admin"])
            .Under(tenant)
            .AddUser(out var other, u => u.Roles = ["Admin"])
            .BuildAsync();
        await scope.AddSignInLinkAsync(owner);
        await scope.AddPasswordAsync(other, stopped: true);
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        var act = () => scope.SendAsync(new RemoveUserPermission.Command(owner.Id, Permission.ManageUserAccess().Value));

        // Assert
        (await act.ShouldThrowAsync<ConflictException>()).Message.ShouldBe(LastAdmin.RefusedMessage);
    }

    [Test]
    [Property("Rule", "PWD-21")]
    public async Task a_user_delete_removes_the_password_of_the_user()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner, u => u.Roles = ["Admin"])
            .Under(tenant)
            .AddUser(out var member, u => u.Roles = [])
            .BuildAsync();
        await scope.AddPasswordAsync(member);
        scope.SetUserWithDefaults(identifier: owner.Identifier);

        // Act
        await scope.SendAsync(new DeleteUser.Command(member.Id));

        // Assert
        (await CredentialCountAsync(scope, member.Id)).ShouldBe(0);
    }

    [Test]
    [Property("Rule", "PWD-21")]
    public async Task a_tenant_delete_removes_the_password_of_each_user()
    {
        // Arrange
        var scope = new TestingServiceScope();
        var scenario = await scope.StartTenantScenarioAsync();
        await scope.AddPasswordAsync(scenario.Admin);
        await scope.AddPasswordAsync(scenario.FirstUser);

        // Act
        await scope.SendAsync(new DeleteTenant.Command(scenario.Tenant.Id));

        // Assert
        (await CredentialCountAsync(scope, scenario.Admin.Id)).ShouldBe(0);
        (await CredentialCountAsync(scope, scenario.FirstUser.Id)).ShouldBe(0);
    }

    [Test]
    [Property("Rule", "PWD-15")]
    public async Task the_database_refuses_a_temporary_password_that_lasts_more_than_7_days()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _).AddTenant(out var tenant).AddUser(out var fits).Under(tenant).AddUser(out var tooLong).BuildAsync();
        LoginName.TryOf($"fits-{fits.Id}", out var fitsName);
        LoginName.TryOf($"long-{tooLong.Id}", out var tooLongName);
        var issuedAt = new DateTimeOffset(2026, 11, 21, 10, 0, 0, 123, 456, TimeSpan.Zero);

        // Act
        var saveFits = () => scope.ExecuteDbContextAsync(db =>
        {
            db.Set<PasswordCredential>().Add(PasswordCredential.IssueTemporary(fits.Id, fitsName!, "hash", issuedAt));
            return db.SaveChangesAsync();
        });
        var saveTooLong = () => scope.ExecuteDbContextAsync(db =>
        {
            var credential = PasswordCredential.IssueTemporary(tooLong.Id, tooLongName!, "hash", issuedAt);
            db.Set<PasswordCredential>().Add(credential);
            db.Entry(credential).Property(c => c.TemporaryUntil).CurrentValue = issuedAt + PasswordCredential.TemporaryLifetime + TimeSpan.FromSeconds(1);
            return db.SaveChangesAsync();
        });

        // Assert
        await saveFits.ShouldNotThrowAsync();
        await saveTooLong.ShouldThrowAsync<DbUpdateException>();
    }

    [Test]
    [Property("Rule", "PWD-18")]
    public async Task two_users_cannot_hold_one_login_name()
    {
        // Arrange
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _).AddTenant(out var tenant).AddUser(out var first).Under(tenant).AddUser(out var second).BuildAsync();
        LoginName.TryOf($"same-{first.Id}", out var name);
        await scope.ExecuteDbContextAsync(db =>
        {
            db.Set<PasswordCredential>().Add(PasswordCredential.Choose(first.Id, name!, "hash", DateTimeOffset.UtcNow));
            return db.SaveChangesAsync();
        });

        // Act
        var act = () => scope.ExecuteDbContextAsync(db =>
        {
            db.Set<PasswordCredential>().Add(PasswordCredential.Choose(second.Id, name!, "hash", DateTimeOffset.UtcNow));
            return db.SaveChangesAsync();
        });

        // Assert
        (await act.ShouldThrowAsync<ConstraintConflictException>()).Constraint.ShouldBe("ix_auth_password_credentials_login_name");
    }

    private static Task<int> CredentialCountAsync(TestingServiceScope scope, string userId)
        => scope.ExecuteDbContextAsync(db => db.Set<PasswordCredential>().CountAsync(p => p.UserId == userId));
}
