namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.Sessions;
using Domain.Tenants;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    private const string RawRevisionsField = "_appendedRawRevisions";
    private const string CleanedRevisionsField = "_appendedCleanedRevisions";

    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.TenantId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.UserId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.ClientSessionId).IsRequired().HasMaxLength(Session.ClientSessionIdMaxLength);
        builder.Property(e => e.Preview).IsRequired().HasMaxLength(Session.PreviewMaxLength);
        builder.Property(e => e.Synopsis).HasMaxLength(Session.SynopsisMaxLength);
        builder.Property(e => e.Activity).IsRequired().HasMaxLength(Session.MetadataValueMaxLength);
        builder.Property(e => e.CleanupRun).HasConversion<string>().HasMaxLength(20);
        builder.PrimitiveCollection(e => e.Moods).HasMaxLength(Session.MetadataListColumnMaxLength);
        builder.PrimitiveCollection(e => e.UserTopics).HasMaxLength(Session.MetadataListColumnMaxLength);
        builder.PrimitiveCollection(e => e.AiTopics).HasMaxLength(Session.MetadataListColumnMaxLength);
        builder.PrimitiveCollection(e => e.MentionedPersonIds).HasMaxLength(Session.MetadataListColumnMaxLength);
        builder.PrimitiveCollection(e => e.MetadataFastClaims).HasMaxLength(FastClockClaims.ColumnMaxLength);

        builder.ComplexProperty(e => e.Raw, slot => ConfigureSlot(slot, "raw"));
        builder.ComplexProperty(e => e.Cleaned, slot => ConfigureSlot(slot, "cleaned"));

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.ClientNoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasMany<RawRevision>(RawRevisionsField).WithOne(r => r.Session).HasForeignKey(r => r.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany<CleanedRevision>(CleanedRevisionsField).WithOne(r => r.Session).HasForeignKey(r => r.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(RawRevisionsField).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(CleanedRevisionsField).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(e => new { e.UserId, e.ClientSessionId }).IsUnique().ForStaleVersion();
        builder.HasIndex(e => new { e.UserId, e.StartedAt, e.Id }).IsDescending(false, true, true);
        builder.HasIndex(e => e.TenantId);
    }

    private static void ConfigureSlot(ComplexPropertyBuilder<DraftSlot> slot, string prefix)
    {
        slot.Property(s => s.Text).HasColumnName($"{prefix}_text").IsRequired().HasMaxLength(Session.DraftMaxLength);
        slot.Property(s => s.DraftRevision).HasColumnName($"{prefix}_draft_revision");
        slot.Property(s => s.LatestRevision).HasColumnName($"{prefix}_latest_revision");
        slot.Property(s => s.SavedAt).HasColumnName($"{prefix}_saved_at");
    }
}
