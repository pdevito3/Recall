namespace Recall.IntegrationTests.Auth;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public sealed class ProvisionerTests
{
    private const string IssuerA = "https://a.example/realms/recall";
    private const string IssuerB = "https://b.example/realms/recall";

    private static ExternalIdentity Person(string subject, string issuer = IssuerA, Profile? profile = null)
        => new(issuer, subject, profile ?? new Profile("Ada", "Lovelace", "Ada Lovelace", "ada@example.com", "ada"));

    private static string UserIdOf(ProvisionOutcome outcome)
        => outcome.ShouldBeOfType<ProvisionOutcome.Provisioned>().UserId;

    private static Provisioner ProvisionerOf(IServiceProvider services) => services.GetRequiredService<Provisioner>();

    [Test]
    public async Task the_first_login_makes_the_tenant_and_the_owner_with_identifier_equal_to_id()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var provisioner = ProvisionerOf(database.BuildServices());

        // Act
        var outcome = await provisioner.ResolveAsync(Person("first"), CancellationToken.None);

        // Assert
        var userId = UserIdOf(outcome);
        var user = await database.ReadAsync(db => db.Users.Include(u => u.UserPermissions).SingleAsync());
        user.Id.ShouldBe(userId);
        user.Identifier.ShouldBe(userId);
        user.Role.Value.ShouldBe(UserRole.Admin().Value);
        user.UserPermissions.ShouldNotBeEmpty();
        user.FirstName.ShouldBe("Ada");
        user.LastName.ShouldBe("Lovelace");
        (await database.ReadAsync(db => db.Tenants.SingleAsync())).Name.ShouldBe("Recall");
        var link = await database.ReadAsync(db => db.Set<ExternalLogin>().SingleAsync());
        (link.Issuer, link.Subject, link.UserId).ShouldBe((IssuerA, "first", userId));
    }

    [Test]
    public async Task the_same_pair_gives_the_same_user_and_the_same_subject_at_a_second_issuer_gives_another()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var provisioner = ProvisionerOf(database.BuildServices(openSignup: true));

        // Act
        var first = UserIdOf(await provisioner.ResolveAsync(Person("same"), CancellationToken.None));
        var again = UserIdOf(await provisioner.ResolveAsync(Person("same"), CancellationToken.None));
        var otherIssuer = UserIdOf(await provisioner.ResolveAsync(Person("same", IssuerB), CancellationToken.None));

        // Assert
        again.ShouldBe(first);
        otherIssuer.ShouldNotBe(first);
        (await database.ReadAsync(db => db.Users.CountAsync())).ShouldBe(2);
        (await database.ReadAsync(db => db.Set<ExternalLogin>().CountAsync())).ShouldBe(2);
    }

    [Test]
    public async Task a_second_subject_gets_signup_closed_and_with_open_signup_joins_as_a_user()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var closed = ProvisionerOf(database.BuildServices(openSignup: false));
        var open = ProvisionerOf(database.BuildServices(openSignup: true));
        var owner = UserIdOf(await closed.ResolveAsync(Person("owner"), CancellationToken.None));

        // Act
        var refused = await closed.ResolveAsync(Person("second"), CancellationToken.None);
        var joined = UserIdOf(await open.ResolveAsync(Person("second"), CancellationToken.None));

        // Assert
        refused.ShouldBeOfType<ProvisionOutcome.SignupClosed>();
        var user = await database.ReadAsync(db => db.Users.SingleAsync(u => u.Id == joined));
        user.Role.Value.ShouldBe(UserRole.User().Value);
        user.Identifier.ShouldBe(joined);
        var ownerRow = await database.ReadAsync(db => db.Users.SingleAsync(u => u.Id == owner));
        user.TenantId.ShouldBe(ownerRow.TenantId);
    }

    [Test]
    public async Task a_deleted_tenant_still_counts_so_no_login_becomes_an_owner()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var services = database.BuildServices(openSignup: true);
        await ProvisionerOf(services).ResolveAsync(Person("owner"), CancellationToken.None);
        await using (var scope = services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync();
            user.Delete();
            db.Users.Remove(user);
            db.Tenants.Remove(await db.Tenants.SingleAsync());
            await db.SaveChangesAsync();
        }

        // Act
        var outcome = await ProvisionerOf(services).ResolveAsync(Person("stranger"), CancellationToken.None);

        // Assert
        outcome.ShouldBeOfType<ProvisionOutcome.SignupClosed>();
        (await database.ReadAsync(db => db.Users.CountAsync())).ShouldBe(0);
    }

    [Test]
    public async Task first_owner_race_with_two_subjects_gives_one_owner_and_signup_closed()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var pause = PauseAtQuery.After("auth.provision.any_tenant_ever");
        var underTest = ProvisionerOf(database.BuildServices(interceptor: pause));
        var rival = ProvisionerOf(database.BuildServices());

        // Act
        var held = underTest.ResolveAsync(Person("held"), CancellationToken.None);
        await pause.ReachedAsync();
        var winner = await rival.ResolveAsync(Person("rival"), CancellationToken.None);
        pause.Release();
        var loser = await held;

        // Assert
        var ownerId = UserIdOf(winner);
        loser.ShouldBeOfType<ProvisionOutcome.SignupClosed>();
        (await database.ReadAsync(db => db.Tenants.CountAsync())).ShouldBe(1);
        (await database.ReadAsync(db => db.Users.SingleAsync())).Id.ShouldBe(ownerId);
        (await database.ReadAsync(db => db.Set<ExternalLogin>().SingleAsync())).Subject.ShouldBe("rival");
    }

    [Test]
    public async Task first_owner_race_with_two_subjects_and_open_signup_gives_an_owner_and_a_user()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var pause = PauseAtQuery.After("auth.provision.any_tenant_ever");
        var underTest = ProvisionerOf(database.BuildServices(openSignup: true, interceptor: pause));
        var rival = ProvisionerOf(database.BuildServices(openSignup: true));

        // Act
        var held = underTest.ResolveAsync(Person("held"), CancellationToken.None);
        await pause.ReachedAsync();
        var ownerId = UserIdOf(await rival.ResolveAsync(Person("rival"), CancellationToken.None));
        pause.Release();
        var userId = UserIdOf(await held);

        // Assert
        userId.ShouldNotBe(ownerId);
        (await database.ReadAsync(db => db.Tenants.CountAsync())).ShouldBe(1);
        (await database.ReadAsync(db => db.Users.SingleAsync(u => u.Id == ownerId))).Role.Value.ShouldBe(UserRole.Admin().Value);
        (await database.ReadAsync(db => db.Users.SingleAsync(u => u.Id == userId))).Role.Value.ShouldBe(UserRole.User().Value);
    }

    [Test]
    public async Task a_rival_first_owner_between_the_tenant_reads_does_not_close_an_open_signup()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var pause = PauseAtQuery.After("auth.provision.oldest_tenant");
        var underTest = ProvisionerOf(database.BuildServices(openSignup: true, interceptor: pause));
        var rival = ProvisionerOf(database.BuildServices(openSignup: true));

        // Act
        var held = underTest.ResolveAsync(Person("held"), CancellationToken.None);
        await pause.ReachedAsync();
        var ownerId = UserIdOf(await rival.ResolveAsync(Person("rival"), CancellationToken.None));
        pause.Release();
        var outcome = await held;

        // Assert
        UserIdOf(outcome).ShouldNotBe(ownerId);
        (await database.ReadAsync(db => db.Tenants.CountAsync())).ShouldBe(1);
    }

    [Test]
    public async Task first_owner_race_with_one_subject_twice_gives_one_user_and_one_link()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var pause = PauseAtQuery.After("auth.provision.any_tenant_ever");
        var underTest = ProvisionerOf(database.BuildServices(interceptor: pause));
        var rival = ProvisionerOf(database.BuildServices());

        // Act
        var held = underTest.ResolveAsync(Person("twice"), CancellationToken.None);
        await pause.ReachedAsync();
        var winner = UserIdOf(await rival.ResolveAsync(Person("twice"), CancellationToken.None));
        pause.Release();
        var loser = UserIdOf(await held);

        // Assert
        loser.ShouldBe(winner);
        (await database.ReadAsync(db => db.Tenants.CountAsync())).ShouldBe(1);
        (await database.ReadAsync(db => db.Users.CountAsync())).ShouldBe(1);
        (await database.ReadAsync(db => db.Set<ExternalLogin>().CountAsync())).ShouldBe(1);
    }

    [Test]
    public async Task many_first_logins_at_the_same_time_give_one_owner()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var provisioner = ProvisionerOf(database.BuildServices());

        // Act
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(i => Task.Run(() => provisioner.ResolveAsync(Person($"subject-{i}"), CancellationToken.None))));

        // Assert
        outcomes.OfType<ProvisionOutcome.Provisioned>().Count().ShouldBe(1);
        outcomes.OfType<ProvisionOutcome.SignupClosed>().Count().ShouldBe(5);
        (await database.ReadAsync(db => db.Tenants.CountAsync())).ShouldBe(1);
        (await database.ReadAsync(db => db.Users.CountAsync())).ShouldBe(1);
    }

    [Test]
    public async Task external_login_race_with_open_signup_gives_one_user_for_one_subject()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var seed = ProvisionerOf(database.BuildServices(openSignup: true));
        await seed.ResolveAsync(Person("owner"), CancellationToken.None);
        var pause = PauseAtQuery.After("auth.provision.tenant_is_live");
        var underTest = ProvisionerOf(database.BuildServices(openSignup: true, interceptor: pause));

        // Act
        var held = underTest.ResolveAsync(Person("joiner"), CancellationToken.None);
        await pause.ReachedAsync();
        var winner = UserIdOf(await seed.ResolveAsync(Person("joiner"), CancellationToken.None));
        pause.Release();
        var loser = UserIdOf(await held);

        // Assert
        loser.ShouldBe(winner);
        (await database.ReadAsync(db => db.Users.CountAsync())).ShouldBe(2);
        (await database.ReadAsync(db => db.Set<ExternalLogin>().CountAsync(l => l.Subject == "joiner"))).ShouldBe(1);
    }

    [Test]
    public async Task a_stale_version_reruns_and_keeps_the_edit_of_the_rival()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var seed = database.BuildServices();
        var noLastName = new Profile("Ada", null, null, null, "ada");
        var userId = UserIdOf(await ProvisionerOf(seed).ResolveAsync(Person("edit", profile: noLastName), CancellationToken.None));
        var pause = PauseAtQuery.After("auth.provision.linked_user");
        var underTest = ProvisionerOf(database.BuildServices(interceptor: pause));

        // Act
        var held = underTest.ResolveAsync(Person("edit"), CancellationToken.None);
        await pause.ReachedAsync();
        await using (var scope = seed.GetRequiredService<IServiceScopeFactory>().CreateSystemScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync();
            user.UpdateFromIdp("Edited", null, null, user.Username);
            await db.SaveChangesAsync();
        }
        pause.Release();
        var outcome = await held;

        // Assert
        UserIdOf(outcome).ShouldBe(userId);
        var after = await database.ReadAsync(db => db.Users.SingleAsync());
        after.FirstName.ShouldBe("Edited");
        after.LastName.ShouldBe("Lovelace");
        after.Email!.Value.ShouldBe("ada@example.com");
    }

    [Test]
    public async Task a_repeat_login_fills_an_empty_value_and_never_overwrites_or_clears()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var provisioner = ProvisionerOf(database.BuildServices());
        await provisioner.ResolveAsync(Person("merge", profile: new Profile("Ada", null, null, null, "ada")), CancellationToken.None);

        // Act
        await provisioner.ResolveAsync(Person("merge", profile: new Profile("Other", "Lovelace", "Other Name", "ada@example.com", "other")), CancellationToken.None);
        var filled = await database.ReadAsync(db => db.Users.SingleAsync());
        await provisioner.ResolveAsync(Person("merge", profile: new Profile(null, null, null, null, null)), CancellationToken.None);
        var kept = await database.ReadAsync(db => db.Users.SingleAsync());
        await provisioner.ResolveAsync(Person("merge", profile: new Profile("Third", "Third", null, "third@example.com", "third")), CancellationToken.None);
        var unchanged = await database.ReadAsync(db => db.Users.SingleAsync());

        // Assert
        filled.FirstName.ShouldBe("Ada");
        filled.Username.ShouldBe("ada");
        filled.LastName.ShouldBe("Lovelace");
        filled.Email!.Value.ShouldBe("ada@example.com");
        kept.LastName.ShouldBe("Lovelace");
        kept.Email!.Value.ShouldBe("ada@example.com");
        unchanged.LastName.ShouldBe("Lovelace");
        unchanged.Email!.Value.ShouldBe("ada@example.com");
    }

    [Test]
    public async Task an_odd_profile_never_fails_a_login()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var provisioner = ProvisionerOf(database.BuildServices());
        var odd = new Profile("  ", "\uD800bad", new string('x', 5000), "not an email", new string('y', 5000));

        // Act
        var outcome = await provisioner.ResolveAsync(Person("odd", profile: odd), CancellationToken.None);

        // Assert
        UserIdOf(outcome).ShouldNotBeNull();
    }

    [Test]
    public async Task the_owner_claim_links_a_new_identity_to_the_oldest_admin_one_time()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var provisioner = ProvisionerOf(database.BuildServices());
        var owner = UserIdOf(await provisioner.ResolveAsync(Person("old-subject"), CancellationToken.None));
        await File.WriteAllTextAsync(database.ClaimFilePath, "");

        // Act
        var claimed = await provisioner.ResolveAsync(Person("new-subject", IssuerB), CancellationToken.None);
        var next = await provisioner.ResolveAsync(Person("another", IssuerB), CancellationToken.None);

        // Assert
        UserIdOf(claimed).ShouldBe(owner);
        next.ShouldBeOfType<ProvisionOutcome.SignupClosed>();
        File.Exists(database.ClaimFilePath).ShouldBeFalse();
        Directory.GetFiles(Path.GetDirectoryName(database.ClaimFilePath)!, OwnerClaim.FileName + "*").ShouldBeEmpty();
        (await database.ReadAsync(db => db.Users.CountAsync())).ShouldBe(1);
    }

    [Test]
    public async Task the_owner_claim_goes_to_one_of_two_logins_at_the_same_time()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var provisioner = ProvisionerOf(database.BuildServices());
        var owner = UserIdOf(await provisioner.ResolveAsync(Person("old-subject"), CancellationToken.None));
        await File.WriteAllTextAsync(database.ClaimFilePath, "");

        // Act
        var outcomes = await Task.WhenAll(
            Task.Run(() => provisioner.ResolveAsync(Person("claim-a", IssuerB), CancellationToken.None)),
            Task.Run(() => provisioner.ResolveAsync(Person("claim-b", IssuerB), CancellationToken.None)));

        // Assert
        outcomes.OfType<ProvisionOutcome.Provisioned>().Single().UserId.ShouldBe(owner);
        outcomes.OfType<ProvisionOutcome.SignupClosed>().Count().ShouldBe(1);
        (await database.ReadAsync(db => db.Set<ExternalLogin>().CountAsync())).ShouldBe(2);
    }

    [Test]
    public async Task a_linked_identity_does_not_take_the_owner_claim()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var provisioner = ProvisionerOf(database.BuildServices());
        await provisioner.ResolveAsync(Person("old-subject"), CancellationToken.None);
        await File.WriteAllTextAsync(database.ClaimFilePath, "");

        // Act
        await provisioner.ResolveAsync(Person("old-subject"), CancellationToken.None);

        // Assert
        File.Exists(database.ClaimFilePath).ShouldBeTrue();
    }

    [Test]
    public async Task the_owner_claim_with_no_live_admin_adds_a_new_admin()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var services = database.BuildServices();
        var provisioner = ProvisionerOf(services);
        var deleted = UserIdOf(await provisioner.ResolveAsync(Person("gone"), CancellationToken.None));
        await using (var scope = services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync();
            user.Delete();
            db.Users.Remove(user);
            await db.SaveChangesAsync();
        }
        await File.WriteAllTextAsync(database.ClaimFilePath, "");

        // Act
        var outcome = UserIdOf(await provisioner.ResolveAsync(Person("back", IssuerB), CancellationToken.None));

        // Assert
        outcome.ShouldNotBe(deleted);
        (await database.ReadAsync(db => db.Users.SingleAsync())).Role.Value.ShouldBe(UserRole.Admin().Value);
    }

    [Test]
    public async Task a_user_delete_removes_links_sessions_and_codes_in_the_same_save()
    {
        // Arrange
        await using var database = await AuthTestDatabase.CreateAsync();
        var services = database.BuildServices(openSignup: true);
        var provisioner = ProvisionerOf(services);
        await provisioner.ResolveAsync(Person("owner"), CancellationToken.None);
        var userId = UserIdOf(await provisioner.ResolveAsync(Person("leaving"), CancellationToken.None));
        await using (var scope = services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<SessionStore>();
            await store.StartWebAsync(userId, "oidc", CancellationToken.None);
            await store.StartNativeAsync(userId, "oidc", CancellationToken.None);
            await store.AddAppCodeAsync(userId, "oidc", "recall://auth/callback", "challenge", null, CancellationToken.None);
        }

        // Act
        await using (var scope = services.GetRequiredService<IServiceScopeFactory>().CreateSystemScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.Delete();
            db.Users.Remove(user);
            await db.SaveChangesAsync();
        }

        // Assert
        (await database.ReadAsync(db => db.Set<ExternalLogin>().CountAsync(l => l.UserId == userId))).ShouldBe(0);
        (await database.ReadAsync(db => db.Set<AuthSession>().CountAsync(s => s.UserId == userId))).ShouldBe(0);
        (await database.ReadAsync(db => db.Set<AppCode>().CountAsync(c => c.UserId == userId))).ShouldBe(0);
        (await database.ReadAsync(db => db.Set<ExternalLogin>().CountAsync())).ShouldBe(1);
    }
}
