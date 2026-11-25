namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.ImportBatches;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ImportDayConfiguration : IEntityTypeConfiguration<ImportDay>
{
    public void Configure(EntityTypeBuilder<ImportDay> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.BatchId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.UserId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);

        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasIndex(e => new { e.BatchId, e.Date }).IsUnique().ForStaleVersion();

        builder.ApplyTenanting(e => e.Batch.TenantId);
    }
}
