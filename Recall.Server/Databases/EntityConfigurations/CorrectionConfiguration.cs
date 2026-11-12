namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.Corrections;
using Domain.Tenants;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class CorrectionConfiguration : IEntityTypeConfiguration<Correction>
{
    public void Configure(EntityTypeBuilder<Correction> builder)
    {
        builder.HasKey(e => e.Id).ForStaleVersion();
        builder.Property(e => e.Id).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.TenantId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.UserId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.CanonicalTerm).IsRequired().HasMaxLength(Correction.CanonicalTermMaxLength);
        builder.PrimitiveCollection(e => e.Mishearings).HasMaxLength(Correction.MishearingsColumnMaxLength);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.ClientNoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasIndex(e => new { e.UserId, e.CanonicalTerm, e.Id });
        builder.HasIndex(e => e.TenantId);
    }
}
