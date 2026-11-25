namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.ImportBatches;
using Domain.Tenants;
using Domain.TimeZones;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.TenantId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.UserId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(ImportBatch.NameMaxLength);
        builder.Property(e => e.TimeZone).IsRequired().HasMaxLength(TimeZoneId.MaxLength);
        builder.Property(e => e.State).HasConversion<string>().HasMaxLength(20);
        builder.ComplexProperty(e => e.Result, result =>
        {
            result.Property(r => r.SessionsMade).HasColumnName("sessions_made");
            result.Property(r => r.EntriesRejected).HasColumnName("entries_rejected");
            result.Property(r => r.DaysRejected).HasColumnName("days_rejected");
            result.Property(r => r.SkippedInJournal).HasColumnName("skipped_in_journal");
            result.Property(r => r.SkippedDeleted).HasColumnName("skipped_deleted");
        });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.ClientNoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasMany(e => e.Days).WithOne(d => d.Batch).HasForeignKey(d => d.BatchId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Days).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany<ImportEntry>().WithOne(e => e.Batch).HasForeignKey(e => e.BatchId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.UserId, e.OpenedAt });
        builder.HasIndex(e => e.TenantId);
    }
}
