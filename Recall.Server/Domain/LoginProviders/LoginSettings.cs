namespace Recall.Server.Domain.LoginProviders;

using AuditEntries;
using Exceptions;

public enum PasswordLoginState
{
    On,
    Off,
}

/// <summary>
/// The login settings of the install, the one row of <c>auth_login_settings</c> (spec <c>login-providers.md</c>, section 2). A migration
/// inserts the row with password login On, so no code adds one. The state changes only through <see cref="TurnPasswordsOff"/> and
/// <see cref="TurnPasswordsOn"/>, and each change moves <see cref="Version"/>, the concurrency token of the row.
/// </summary>
[NotAudited("One install-wide row with no tenant. The audit log records each turn of password login as an action.")]
public sealed class LoginSettings
{
    /// <summary>The key of the one row.</summary>
    public const int RowId = 1;

    public const string PasswordLoginField = "passwordLogin";

    public const string KeepsNoLinkedAdminMessage = "Each tenant needs an Admin who can sign in with a provider before password login turns off.";

    private LoginSettings()
    {
    }

    public int Id { get; private set; }
    public PasswordLoginState PasswordLogin { get; private set; }
    public uint Version { get; private set; }

    /// <summary>The row that the migration inserts.</summary>
    public static LoginSettings Seed() => new() { Id = RowId, PasswordLogin = PasswordLoginState.On, Version = 1 };

    /// <summary>
    /// Turns password login Off (IDP-13). <paramref name="eachTenantHasALinkedAdmin"/> is the proof of <c>LastAdmin.EachTenantHasALinkedAdmin</c>.
    /// Null when password login is Off already, so the turn records nothing.
    /// </summary>
    /// <exception cref="StaleVersionException">The row changed after the caller read <paramref name="version"/>.</exception>
    /// <exception cref="ConflictException">A live tenant has no Admin who holds each Admin permission and has a link to a Usable provider.</exception>
    public PasswordLoginTurnedOff? TurnPasswordsOff(uint version, bool eachTenantHasALinkedAdmin, AuditSubject subject)
    {
        GuardVersion(version);
        if (PasswordLogin == PasswordLoginState.Off)
            return null;
        if (!eachTenantHasALinkedAdmin)
            throw new ConflictException(string.Empty, KeepsNoLinkedAdminMessage);

        PasswordLogin = PasswordLoginState.Off;
        Version++;
        return new PasswordLoginTurnedOff(subject);
    }

    /// <summary>Turns password login On, which always succeeds (IDP-13). Null when it is On already, so the turn records nothing.</summary>
    /// <exception cref="StaleVersionException">The row changed after the caller read <paramref name="version"/>.</exception>
    public PasswordLoginTurnedOn? TurnPasswordsOn(uint version, AuditSubject subject)
    {
        GuardVersion(version);
        if (PasswordLogin == PasswordLoginState.On)
            return null;

        PasswordLogin = PasswordLoginState.On;
        Version++;
        return new PasswordLoginTurnedOn(subject);
    }

    private void GuardVersion(uint version)
    {
        if (version != Version)
            throw new StaleVersionException();
    }

    public override string ToString() => $"[login settings, password login {PasswordLogin}]";
}

// The settings are install-wide, so each turn is recorded on the tenant of the Admin who made it, as an action on a login provider is.

[AuditActionName("password_login_turned_off")]
public sealed record PasswordLoginTurnedOff(AuditSubject Subject) : AuditAction(Subject);

[AuditActionName("password_login_turned_on")]
public sealed record PasswordLoginTurnedOn(AuditSubject Subject) : AuditAction(Subject);
