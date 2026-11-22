namespace Recall.UnitTests.Domain.LoginProviders;

using Recall.Server.Domain.AuditEntries;
using Recall.Server.Domain.LoginProviders;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Tenants.Models;
using Recall.Server.Exceptions;
using Shouldly;

public class LoginSettingsTests
{
    private static readonly AuditSubject Subject = AuditSubject.Of(Tenant.Create(new TenantForCreation { Name = "Recall" }));

    [Test]
    [Property("Rule", "IDP-13")]
    public void a_turn_off_with_no_linked_admin_in_a_tenant_gets_a_conflict_and_changes_nothing()
    {
        // Arrange
        var settings = LoginSettings.Seed();

        // Act
        var turn = () => settings.TurnPasswordsOff(settings.Version, eachTenantHasALinkedAdmin: false, Subject);

        // Assert
        turn.ShouldThrow<ConflictException>().Message.ShouldBe(LoginSettings.KeepsNoLinkedAdminMessage);
        settings.PasswordLogin.ShouldBe(PasswordLoginState.On);
        settings.Version.ShouldBe(1u);
    }

    [Test]
    [Property("Rule", "IDP-13")]
    public void a_turn_off_then_on_records_each_action_and_moves_the_version()
    {
        // Arrange
        var settings = LoginSettings.Seed();

        // Act
        var off = settings.TurnPasswordsOff(1, eachTenantHasALinkedAdmin: true, Subject);
        var offAgain = settings.TurnPasswordsOff(2, eachTenantHasALinkedAdmin: false, Subject);
        var on = settings.TurnPasswordsOn(2, Subject);

        // Assert
        off.ShouldNotBeNull();
        offAgain.ShouldBeNull();
        on.ShouldNotBeNull();
        settings.PasswordLogin.ShouldBe(PasswordLoginState.On);
        settings.Version.ShouldBe(3u);
    }

    [Test]
    [Property("Rule", "IDP-13")]
    public void a_turn_with_a_stale_version_gets_a_conflict_before_the_rule()
    {
        // Arrange
        var settings = LoginSettings.Seed();

        // Act
        var off = () => settings.TurnPasswordsOff(7, eachTenantHasALinkedAdmin: false, Subject);
        var on = () => settings.TurnPasswordsOn(7, Subject);

        // Assert
        off.ShouldThrow<StaleVersionException>();
        on.ShouldThrow<StaleVersionException>();
    }
}
