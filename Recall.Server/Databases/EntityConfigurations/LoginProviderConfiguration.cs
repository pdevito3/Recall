namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.LoginProviders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class LoginProviderConfiguration : IEntityTypeConfiguration<LoginProvider>
{
    public void Configure(EntityTypeBuilder<LoginProvider> builder)
    {
        builder.ToTable("auth_oidc_providers");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever().HasMaxLength(LoginProvider.IdMaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.DisplayName).IsRequired().HasMaxLength(LoginProvider.DisplayNameMaxLength);
        builder.Property(e => e.Issuer).IsRequired().HasMaxLength(LoginProvider.IssuerMaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.ClientId).IsRequired().HasMaxLength(LoginProvider.ClientIdMaxLength);
        builder.Property(e => e.ProtectedClientSecret).IsRequired().HasMaxLength(LoginProvider.ProtectedClientSecretMaxLength);
        builder.Property(e => e.CaCertificatePath).HasMaxLength(LoginProvider.CaCertificatePathMaxLength);
        builder.Property(e => e.Version).IsConcurrencyToken();

        // The handlers check both values first. The indexes stop two adds that race (IDP-3). The index on the key gives a
        // repeated ID its message, because a save that the key alone stops names no request field.
        builder.HasIndex(e => e.Id).IsUnique().ForRequestField(LoginProvider.IdField, LoginProvider.IdInUseMessage);
        builder.HasIndex(e => e.Issuer).IsUnique().ForRequestField(LoginProvider.IssuerField, LoginProvider.IssuerInUseMessage);
        builder.HasIndex(e => e.CreatedAt);
    }
}
