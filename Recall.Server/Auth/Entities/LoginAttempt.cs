namespace Recall.Server.Auth.Entities;

using Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

[NotAudited("A login in flight. A row holds no business data, and the callback or the sweep removes it within minutes.")]
public sealed record LoginAttempt
{
    public const int ModeIdMaxLength = AuthSession.LoginModeMaxLength;
    public const int SecretMaxLength = 64;
    public const int RedirectUriMaxLength = 2048;
    public const int ReturnPathMaxLength = 2048;
    public const int NativeRedirectMaxLength = 512;
    public const int NativeChallengeMaxLength = 128;
    public const int NativeStateMaxLength = 512;

    public required string StateHash { get; init; }
    public required string BindingHash { get; init; }
    public required string ModeId { get; init; }
    public required string Nonce { get; init; }
    public required string PkceVerifier { get; init; }
    public required string RedirectUri { get; init; }
    public required SessionKind ClientKind { get; init; }
    public string? ReturnPath { get; init; }
    public string? NativeRedirect { get; init; }
    public string? NativeChallenge { get; init; }
    public string? NativeState { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    public override string ToString() => "[login attempt]";
}

public sealed class LoginAttemptConfiguration : IEntityTypeConfiguration<LoginAttempt>
{
    public void Configure(EntityTypeBuilder<LoginAttempt> builder)
    {
        builder.ToTable("auth_login_attempts", table =>
            table.HasCheckConstraint("ck_auth_login_attempts_client_kind", "client_kind IN ('web', 'native')"));

        builder.HasKey(e => e.StateHash);
        builder.Property(e => e.StateHash).HasMaxLength(AuthSession.HashLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.BindingHash).HasMaxLength(AuthSession.HashLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.ModeId).HasMaxLength(LoginAttempt.ModeIdMaxLength);
        builder.Property(e => e.Nonce).HasMaxLength(LoginAttempt.SecretMaxLength);
        builder.Property(e => e.PkceVerifier).HasMaxLength(LoginAttempt.SecretMaxLength);
        builder.Property(e => e.RedirectUri).HasMaxLength(LoginAttempt.RedirectUriMaxLength);
        builder.Property(e => e.ClientKind).HasConversion(SessionKindText.Converter).HasMaxLength(SessionKindText.MaxLength);
        builder.Property(e => e.ReturnPath).HasMaxLength(LoginAttempt.ReturnPathMaxLength);
        builder.Property(e => e.NativeRedirect).HasMaxLength(LoginAttempt.NativeRedirectMaxLength);
        builder.Property(e => e.NativeChallenge).HasMaxLength(LoginAttempt.NativeChallengeMaxLength);
        builder.Property(e => e.NativeState).HasMaxLength(LoginAttempt.NativeStateMaxLength);

        builder.HasIndex(e => e.ExpiresAt);
    }
}
