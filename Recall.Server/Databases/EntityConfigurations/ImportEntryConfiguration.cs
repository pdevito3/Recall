namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.ImportBatches;
using Domain.Sessions;
using Domain.TimeZones;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ImportEntryConfiguration : IEntityTypeConfiguration<ImportEntry>
{
    public void Configure(EntityTypeBuilder<ImportEntry> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.BatchId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.UserId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.ClientSessionId).IsRequired().HasMaxLength(Session.ClientSessionIdMaxLength);

        // The migration indexes batch_id, date, and position, because EF Core 10 cannot index a column of a complex property.
        builder.ComplexProperty(e => e.Agent, agent =>
        {
            agent.Property(a => a.Date).HasColumnName("date");
            agent.Property(a => a.Position).HasColumnName("position");
            agent.Property(a => a.Time).HasColumnName("time");
            agent.Property(a => a.TimeZone).HasColumnName("time_zone").HasMaxLength(TimeZoneId.MaxLength);
            agent.Property(a => a.Raw).HasColumnName("raw").IsRequired().HasMaxLength(ImportAgentLayer.RawMaxLength);
            agent.PrimitiveCollection(a => a.Moods).HasColumnName("moods").HasMaxLength(Session.MetadataListColumnMaxLength);
            agent.PrimitiveCollection(a => a.Topics).HasColumnName("topics").HasMaxLength(Session.MetadataListColumnMaxLength);
        });
        builder.ComplexProperty(e => e.Review, review =>
        {
            review.Property(r => r.Rejected).HasColumnName("review_rejected");
            review.Property(r => r.Words).HasColumnName("review_words").HasMaxLength(Session.DraftMaxStoredLength);
            review.Property(r => r.Time).HasColumnName("review_time");
        });

        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasIndex(e => new { e.UserId, e.ClientSessionId }).IsUnique().ForStaleVersion();

        builder.ApplyTenanting(e => e.Batch.TenantId);
    }
}
