namespace Recall.Server.Auth.Entities;

using Domain;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>The one-time code that the app trades for its first token pair. Each row lives 2 minutes.</summary>
[NotAudited("A one-time code of a native login. A row holds a hash and no business data, and it lives for minutes.")]
public sealed record AppCode
{
    public required string CodeHash { get; init; }
    public required string UserId { get; init; }
    public required string LoginMode { get; init; }
    public required string RedirectUri { get; init; }
    public required string Challenge { get; init; }
    public string? ClientState { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    public override string ToString() => "[app code]";
}

public sealed class AppCodeConfiguration : IEntityTypeConfiguration<AppCode>
{
    public void Configure(EntityTypeBuilder<AppCode> builder)
    {
        builder.ToTable("auth_app_codes");
        builder.HasKey(e => e.CodeHash);
        builder.Property(e => e.CodeHash).HasMaxLength(AuthSession.HashLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.UserId).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.LoginMode).HasMaxLength(AuthSession.LoginModeMaxLength);
        builder.Property(e => e.RedirectUri).HasMaxLength(LoginAttempt.NativeRedirectMaxLength);
        builder.Property(e => e.Challenge).HasMaxLength(LoginAttempt.NativeChallengeMaxLength);
        builder.Property(e => e.ClientState).HasMaxLength(LoginAttempt.NativeStateMaxLength);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasIndex(e => e.ExpiresAt);
    }
}
