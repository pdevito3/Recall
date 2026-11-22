namespace Recall.Server.Auth.Entities;

using Domain;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

[NotAudited("The audit log records a link and a removal as ExternalLoginLinked and ExternalLoginRemoved on the user, with the issuer, subject, and display_label. A first login adds a row with its new user, and each login moves last_login_at.")]
public sealed record ExternalLogin
{
    public const int IssuerMaxLength = 2048;
    public const int SubjectMaxLength = 255;
    public const int DisplayLabelMaxLength = 320;

    public required string Issuer { get; init; }
    public required string Subject { get; init; }
    public required string UserId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastLoginAt { get; set; }

    /// <summary>The email, else the name, that the provider gave at the link, so the person can tell which account is linked. Null for a link that a first login added before this column.</summary>
    public string? DisplayLabel { get; init; }

    public override string ToString() => $"[external login of {UserId}]";
}

public sealed class ExternalLoginConfiguration : IEntityTypeConfiguration<ExternalLogin>
{
    public void Configure(EntityTypeBuilder<ExternalLogin> builder)
    {
        builder.ToTable("auth_external_logins");
        builder.HasKey(e => new { e.Issuer, e.Subject });
        builder.Property(e => e.Issuer).HasMaxLength(ExternalLogin.IssuerMaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.Subject).HasMaxLength(ExternalLogin.SubjectMaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.UserId).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.DisplayLabel).HasMaxLength(ExternalLogin.DisplayLabelMaxLength);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.ClientNoAction);
    }
}
