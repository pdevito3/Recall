namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.Sessions;
using Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class SyncChangeConfiguration : IEntityTypeConfiguration<SyncChange>
{
    public void Configure(EntityTypeBuilder<SyncChange> builder)
    {
        builder.HasKey(e => e.Sequence);
        builder.Property(e => e.Sequence).ValueGeneratedOnAdd().HasAnnotation("Sqlite:Autoincrement", true);
        builder.Property(e => e.UserId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.EntityType).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.EntityId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.ClientSessionId).HasMaxLength(Session.ClientSessionIdMaxLength);

        builder.HasIndex(e => new { e.EntityType, e.EntityId }).IsUnique().ForStaleVersion();
        builder.HasIndex(e => new { e.UserId, e.Sequence });
        builder.HasIndex(e => new { e.UserId, e.ClientSessionId }).HasFilter("is_tombstone AND client_session_id IS NOT NULL");
    }
}
