namespace Recall.Server.Databases.EntityConfigurations;

using Domain.ModelProviders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class KeyRingMarkerConfiguration : IEntityTypeConfiguration<KeyRingMarker>
{
    public void Configure(EntityTypeBuilder<KeyRingMarker> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.ProtectedValue).IsRequired().HasMaxLength(KeyRingMarker.ProtectedValueMaxLength);
    }
}
