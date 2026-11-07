namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.ModelProviders;
using Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ModelProviderConfiguration : IEntityTypeConfiguration<ModelProvider>
{
    public void Configure(EntityTypeBuilder<ModelProvider> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.TenantId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(ModelProvider.NameMaxLength);
        builder.Property(e => e.BaseUrl).HasMaxLength(ModelProvider.BaseUrlMaxLength);
        builder.Property(e => e.ProtectedApiKey).HasMaxLength(ModelProvider.ProtectedApiKeyMaxLength);
        builder.Property(e => e.Model).HasMaxLength(ModelProvider.ModelMaxLength);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasIndex(e => new { e.TenantId, e.ActivatedOn });
    }
}
