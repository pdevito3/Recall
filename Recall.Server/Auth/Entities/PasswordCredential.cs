namespace Recall.Server.Auth.Entities;

using Databases;
using Domain;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public enum PasswordState
{
    Temporary,
    Chosen,
    Stopped,
}

/// <summary>
/// The version of a credential that a password sign-in saw. The save that starts its session or app code moves the version
/// only while the row is still at it, so a new temporary password in between makes that save stale (PWD-14).
/// </summary>
public sealed record CredentialGuard(string UserId, int Version);

/// <summary>What one typed password does to a credential.</summary>
public enum PasswordVerdict
{
    Wrong,
    Stopped,
    Expired,
    MustChoose,
    SignedIn,
}

/// <summary>
/// The password of one user (spec <c>password-login.md</c>, section 2). The state comes from the columns: Stopped at
/// <see cref="StopAt"/> wrong passwords in a row, else Temporary while <see cref="TemporaryUntil"/> is set, else Chosen.
/// Each change moves <see cref="Version"/>, the concurrency token of the row, so a save of a credential that another write
/// changed after the load throws a <c>StaleVersionException</c> and writes nothing.
/// </summary>
[NotAudited("A login secret of a user. The audit log records each change as an action on the user, and the row must hold no value that an audit entry copies.")]
public sealed class PasswordCredential
{
    public const int HashMaxLength = 256;
    public const int StopAt = 100;

    public static readonly TimeSpan TemporaryLifetime = TimeSpan.FromDays(7);

    private PasswordCredential()
    {
    }

    public string UserId { get; private set; } = default!;
    public LoginName LoginName { get; private set; } = default!;
    public string PasswordHash { get; private set; } = default!;
    public DateTimeOffset ChangedAt { get; private set; }
    public int FailedCount { get; private set; }
    public DateTimeOffset? TemporaryUntil { get; private set; }

    /// <summary>Moves on each change of the row. A choose attempt holds the version that it was written for (PWD-16).</summary>
    public int Version { get; private set; }

    public PasswordState State => FailedCount >= StopAt ? PasswordState.Stopped
        : TemporaryUntil is not null ? PasswordState.Temporary
        : PasswordState.Chosen;

    /// <summary>A Temporary credential whose 7 days have passed (PWD-15). Expired is not a stored state.</summary>
    public bool IsExpired(DateTimeOffset now) => State == PasswordState.Temporary && TemporaryUntil <= now;

    /// <summary>The first temporary password of a user. It also sets the login name.</summary>
    public static PasswordCredential IssueTemporary(string userId, LoginName loginName, string passwordHash, DateTimeOffset now)
    {
        var credential = new PasswordCredential { UserId = userId, LoginName = loginName };
        credential.IssueTemporary(passwordHash, now);
        return credential;
    }

    /// <summary>A new temporary password from any state. It ends a stop, and the login name stays.</summary>
    public void IssueTemporary(string passwordHash, DateTimeOffset now)
    {
        PasswordHash = passwordHash;
        ChangedAt = now;
        TemporaryUntil = now + TemporaryLifetime;
        FailedCount = 0;
        Version++;
    }

    /// <summary>The credential of a person who chooses the first password, as at the setup of the install.</summary>
    public static PasswordCredential Choose(string userId, LoginName loginName, string passwordHash, DateTimeOffset now)
        => new() { UserId = userId, LoginName = loginName, PasswordHash = passwordHash, ChangedAt = now, Version = 1 };

    /// <summary>The person signed in with the temporary password and chose a password (PWD-16).</summary>
    /// <exception cref="InvalidOperationException">The credential is not Temporary, or it expired.</exception>
    public void Choose(string passwordHash, DateTimeOffset now)
    {
        if (State != PasswordState.Temporary || IsExpired(now))
            throw new InvalidOperationException("Only a Temporary credential that has not expired can take a chosen password.");

        PasswordHash = passwordHash;
        ChangedAt = now;
        TemporaryUntil = null;
        FailedCount = 0;
        Version++;
    }

    /// <summary>A wrong password (PWD-13). The count stops at <see cref="StopAt"/>, where the credential is Stopped.</summary>
    public void NoteFailure()
    {
        if (FailedCount < StopAt)
            FailedCount++;
        Version++;
    }

    /// <summary>A right password. It resets the count of wrong passwords in a row, and it moves the version also when the count was 0, so the save of the sign-in checks that the row is still as read.</summary>
    /// <exception cref="InvalidOperationException">The credential is Stopped, so no password can be right.</exception>
    public void NoteSuccess()
    {
        if (State == PasswordState.Stopped)
            throw new InvalidOperationException("A Stopped credential takes no password.");

        FailedCount = 0;
        Version++;
    }

    /// <summary>
    /// Takes the check of one typed password and notes it. A Stopped credential refuses each password, also the right one
    /// (PWD-13). A right temporary password signs in to nothing but the choice of a password, and only for 7 days (PWD-15).
    /// </summary>
    public PasswordVerdict Answer(PasswordCheck check, DateTimeOffset now)
    {
        if (State == PasswordState.Stopped)
            return PasswordVerdict.Stopped;

        if (check == PasswordCheck.Wrong)
        {
            NoteFailure();
            return State == PasswordState.Stopped ? PasswordVerdict.Stopped : PasswordVerdict.Wrong;
        }

        NoteSuccess();
        return State == PasswordState.Chosen ? PasswordVerdict.SignedIn
            : IsExpired(now) ? PasswordVerdict.Expired
            : PasswordVerdict.MustChoose;
    }

    /// <summary>A session or an app code starts from this credential, in the same save, so a change after the sign-in makes that save stale.</summary>
    public void NoteSessionStart() => Version++;

    public CredentialGuard Guard => new(UserId, Version);

    /// <summary>The same password at the current cost of the hash (PWD-10). The time of the change stays.</summary>
    public void Rehash(string passwordHash)
    {
        PasswordHash = passwordHash;
        Version++;
    }

    public override string ToString() => $"[password credential of {UserId}]";
}

public sealed class PasswordCredentialConfiguration : IEntityTypeConfiguration<PasswordCredential>
{
    public void Configure(EntityTypeBuilder<PasswordCredential> builder)
    {
        // The text form of an instant sorts as the instant, and strftime keeps three digits of a second, which only widens the limit by less than a millisecond.
        builder.ToTable("auth_password_credentials", table => table.HasCheckConstraint(
            "ck_auth_password_credentials_temporary_until",
            "temporary_until IS NULL OR temporary_until <= strftime('%Y-%m-%dT%H:%M:%fZ', changed_at, '+7 days')"));

        builder.HasKey(e => e.UserId);
        builder.Property(e => e.UserId).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.LoginName)
            .HasConversion(name => name.Value, value => LoginName.FromStored(value))
            .HasMaxLength(LoginName.MaxStoredLength)
            .UseCollation(EntityId.Collation);
        builder.Property(e => e.PasswordHash).HasMaxLength(PasswordCredential.HashMaxLength);
        builder.Property(e => e.Version).IsConcurrencyToken();
        builder.HasIndex(e => e.LoginName).IsUnique().ForRequestField("loginName", LoginName.InUseMessage);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.ClientNoAction);
    }
}
