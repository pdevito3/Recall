namespace Recall.Server.Databases.EntityConfigurations;

using Domain;
using Domain.People;
using Domain.Tenants;
using Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.TenantId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.UserId).IsRequired().HasMaxLength(EntityId.MaxLength).UseCollation(EntityId.Collation);
        builder.Property(e => e.Label).IsRequired().HasMaxLength(Person.LabelMaxLength);
        builder.Property(e => e.ProposalKey).HasMaxLength(Person.LabelMaxLength);
        builder.Property(e => e.FullName).HasMaxLength(Person.FullNameMaxLength);
        builder.Property(e => e.Notes).HasMaxLength(Person.NotesMaxLength);

        builder.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.ClientNoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.ClientNoAction);

        builder.HasIndex(e => new { e.UserId, e.Label, e.Id });
        builder.HasIndex(e => e.TenantId);

        builder.HasIndex(e => new { e.UserId, e.ProposalKey })
            .IsUnique()
            .HasFilter("proposal_key IS NOT NULL AND is_deleted = false")
            .ForStaleVersion();
    }
}
