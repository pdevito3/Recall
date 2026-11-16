namespace Recall.Server.Auth.Entities;

using Databases;
using Domain;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public enum SessionKind
{
    Web,
    Native,
    Pat,
}

public enum RevokeReason
{
    Logout,
    RefreshReplay,
    RevokeOthers,
    TokenRevoked,
}

[NotAudited("One row for each signed-in device and each personal access token, written at login, at each refresh, and at the create of a token. It holds hashes and no business data. The audit actions on the user record the create and the revoke of a token.")]
public sealed record AuthSession
{
    public const string IdPrefix = "ses";
    public const int HashLength = 64;
    public const int LoginModeMaxLength = 100;
    public const int NameMaxLength = 100;
    public const int GrantMaxLength = 1000;
    public const string TokenInUseMessage = "This sign-in did not work. Please sign in again.";

    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required SessionKind ClientKind { get; init; }
    public required string LoginMode { get; init; }
    public required string AccessHash { get; init; }
    public required DateTimeOffset AccessExpiresAt { get; init; }
    public string? RefreshFamilyHash { get; init; }
    public string? RefreshHash { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSlidAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public RevokeReason? RevokedReason { get; init; }
    public string? Name { get; init; }
    public string? Grant { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }

    public override string ToString() => $"[session {Id}]";
}

public sealed class AuthSessionConfiguration : IEntityTypeConfiguration<AuthSession>
{
    public void Configure(EntityTypeBuilder<AuthSession> builder)
    {
        builder.ToTable("auth_sessions", table =>
        {
            table.HasCheckConstraint("ck_auth_sessions_client_kind", "client_kind IN ('web', 'native', 'pat')");
            table.HasCheckConstraint(
                "ck_auth_sessions_refresh_state",
                "(client_kind IN ('web', 'pat') AND refresh_family_hash IS NULL AND refresh_hash IS NULL) "
                + "OR (client_kind = 'native' AND refresh_family_hash IS NOT NULL AND refresh_hash IS NOT NULL)");
            table.HasCheckConstraint("ck_auth_sessions_name", "(client_kind = 'pat') = (name IS NOT NULL)");
            table.HasCheckConstraint("ck_auth_sessions_grant", "(client_kind = 'pat') = (\"grant\" IS NOT NULL)");
            table.HasCheckConstraint("ck_auth_sessions_last_used_at", "client_kind = 'pat' OR last_used_at IS NULL");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.UserId).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.ClientKind).HasConversion(SessionKindText.Converter).HasMaxLength(SessionKindText.MaxLength);
        builder.Property(e => e.LoginMode).HasMaxLength(AuthSession.LoginModeMaxLength);
        builder.Property(e => e.AccessHash).HasMaxLength(AuthSession.HashLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.RefreshFamilyHash).HasMaxLength(AuthSession.HashLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.RefreshHash).HasMaxLength(AuthSession.HashLength).UseCollation(EntityId.Collation);
        // A tracked revoke or delete changes the row only while its revoke is as read. A revoke that lost a race records no
        // action, and a user delete that lost one to a revoke runs again.
        builder.Property(e => e.RevokedAt).IsConcurrencyToken();
        builder.Property(e => e.RevokedReason).HasConversion(RevokeReasonText.Converter).HasMaxLength(RevokeReasonText.MaxLength);
        builder.Property(e => e.Name).HasMaxLength(AuthSession.NameMaxLength);
        builder.Property(e => e.Grant).HasMaxLength(AuthSession.GrantMaxLength);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasIndex(e => e.AccessHash)
            .IsUnique()
            .HasDatabaseName("ux_auth_sessions_access_hash")
            .ForRequestField("session", AuthSession.TokenInUseMessage);
        builder.HasIndex(e => e.RefreshFamilyHash)
            .IsUnique()
            .HasFilter("refresh_family_hash IS NOT NULL")
            .HasDatabaseName("ux_auth_sessions_refresh_family")
            .ForRequestField("session", AuthSession.TokenInUseMessage);
        builder.HasIndex(e => e.UserId);
        builder.HasIndex(e => e.ExpiresAt).HasDatabaseName("ix_auth_sessions_sweep");
    }
}
