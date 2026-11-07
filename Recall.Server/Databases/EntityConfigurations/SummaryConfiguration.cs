namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.Summaries;
using Domain.Tenants;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class SummaryConfiguration : IEntityTypeConfiguration<Summary>
{
    private const int EnumColumnLength = 30;

    public void Configure(EntityTypeBuilder<Summary> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.TenantId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.UserId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.Period).HasConversion<string>().HasMaxLength(EnumColumnLength);
        builder.Property(e => e.RunState).HasConversion<string>().HasMaxLength(EnumColumnLength);
        builder.Property(e => e.Failure).HasConversion<string>().HasMaxLength(EnumColumnLength);
        builder.Property(e => e.Content).HasMaxLength(Summary.ContentMaxLength);
        builder.Property(e => e.SourceFingerprint).HasMaxLength(Summary.FingerprintLength);
        builder.Property(e => e.RunId).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Ignore(e => e.Key);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.ClientNoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasIndex(e => new { e.UserId, e.Period, e.StartDate }).IsUnique().ForStaleVersion();
        builder.HasIndex(e => new { e.UserId, e.RunJobId });
        builder.HasIndex(e => e.TenantId);
    }
}
