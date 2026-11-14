namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.Recordings;
using Domain.Sessions;
using Domain.Tenants;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class RecordingConfiguration : IEntityTypeConfiguration<Recording>
{
    private const int EnumColumnLength = 30;

    public void Configure(EntityTypeBuilder<Recording> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.TenantId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.UserId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.SessionId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.ClientRecordingId).IsRequired().HasMaxLength(Recording.ClientRecordingIdMaxLength);
        builder.Property(e => e.Format).HasConversion<string>().HasMaxLength(EnumColumnLength);
        builder.Property(e => e.Engine).HasConversion<string>().HasMaxLength(EnumColumnLength);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(EnumColumnLength);
        builder.Property(e => e.Problem).HasConversion<string>().HasMaxLength(EnumColumnLength);
        builder.PrimitiveCollection(e => e.ParagraphTapsMs).HasMaxLength(Recording.ParagraphTapsColumnMaxLength);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.ClientNoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.ClientNoAction);
        builder.HasOne<Session>().WithMany().HasForeignKey(e => e.SessionId).OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasIndex(e => new { e.UserId, e.ClientRecordingId }).IsUnique().ForStaleVersion();
        builder.HasIndex(e => new { e.UserId, e.Status });
        builder.HasIndex(e => e.SessionId);
        builder.HasIndex(e => e.TenantId);
    }
}

public sealed class RecordingSliceConfiguration : IEntityTypeConfiguration<RecordingSlice>
{
    public void Configure(EntityTypeBuilder<RecordingSlice> builder)
    {
        builder.HasKey(e => new { e.RecordingId, e.Index }).ForStaleVersion();
        builder.Property(e => e.RecordingId).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.Index).ValueGeneratedNever();
        builder.Property(e => e.Sha256).IsRequired().HasMaxLength(RecordingSlice.Sha256HexLength);

        builder.HasOne(e => e.Recording).WithMany().HasForeignKey(e => e.RecordingId).OnDelete(DeleteBehavior.Cascade);

        builder.ApplyTenanting(e => e.Recording.TenantId);
        builder.ApplyJournalOwner(e => e.Recording.UserId);
    }
}
