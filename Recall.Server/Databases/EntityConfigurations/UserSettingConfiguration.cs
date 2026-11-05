namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.Sessions;
using Domain.Tenants;
using Domain.TimeZones;
using Domain.Users;
using Domain.UserSettings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class UserSettingConfiguration : IEntityTypeConfiguration<UserSetting>
{
    public void Configure(EntityTypeBuilder<UserSetting> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.TenantId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.UserId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.TimeZone).HasMaxLength(TimeZoneId.MaxLength);
        builder.PrimitiveCollection(e => e.FastClaims).HasMaxLength(FastClockClaims.ColumnMaxLength);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.ClientNoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasIndex(e => e.UserId).IsUnique().ForStaleVersion();
        builder.HasIndex(e => e.TenantId);
    }
}
