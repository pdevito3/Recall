namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class RawRevisionConfiguration : IEntityTypeConfiguration<RawRevision>
{
    public void Configure(EntityTypeBuilder<RawRevision> builder)
    {
        builder.HasKey(e => new { e.SessionId, e.Number }).ForStaleVersion();
        builder.Property(e => e.SessionId).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.Number).ValueGeneratedNever();
        builder.Property(e => e.Text).IsRequired().HasMaxLength(Session.DraftMaxStoredLength);

        builder.ApplyTenanting(e => e.Session.TenantId);
        builder.ApplyJournalOwner(e => e.Session.UserId);
    }
}
