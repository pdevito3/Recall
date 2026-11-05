namespace Recall.Server.Auth.Entities;

using Domain;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

[NotAudited("A link that the login writes for the person who signs in. The audit log records the user, and the row holds no business data.")]
public sealed record ExternalLogin
{
    public const int IssuerMaxLength = 2048;
    public const int SubjectMaxLength = 255;

    public required string Issuer { get; init; }
    public required string Subject { get; init; }
    public required string UserId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastLoginAt { get; set; }

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

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.ClientNoAction);
    }
}
