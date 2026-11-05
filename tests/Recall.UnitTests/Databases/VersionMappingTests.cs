namespace Recall.UnitTests.Databases;

using Recall.Server.Databases;
using Recall.Server.Domain;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

public class VersionMappingTests
{
    [Test]
    public void a_type_that_shares_the_table_of_an_entity_is_refused()
    {
        // Arrange
        using var context = new TableSplittingContext();

        // Act
        var act = () => context.Model;

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldStartWith("ShipmentDetail shares the table shipments of the BaseEntity Shipment.");
    }

    [Test]
    public void each_entity_type_of_a_tpc_hierarchy_with_an_abstract_root_has_a_version_that_the_app_writes()
    {
        // Arrange
        using var context = new AbstractTpcRootContext();

        // Act
        var versions = new[] { typeof(Note), typeof(TextNote) }
            .Select(type => context.Model.FindEntityType(type)!)
            .Select(e => e.FindProperty(nameof(BaseEntity.Version))!)
            .ToList();

        // Assert
        versions.ShouldAllBe(v => v.IsConcurrencyToken && v.ValueGenerated == ValueGenerated.Never);
    }

    private abstract class Note() : BaseEntity("note");

    private sealed class TextNote : Note;

    private sealed class AbstractTpcRootContext() : AppDbContext(TestOptions,
        TimeProvider.System, Substitute.For<ICurrentUserService>(), Substitute.For<ICallerIdentityResolver>(),
        Substitute.For<ITenantScopeProvider>(),
        new DomainEventDispatcher(new ServiceCollection().BuildServiceProvider()))
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.IgnoreTheAppEntities();
            modelBuilder.Entity<Note>(note =>
            {
                note.HasKey(n => n.Id);
                note.UseTpcMappingStrategy();
            });
            modelBuilder.Entity<TextNote>().HasBaseType<Note>();
        }
    }

    private static readonly DbContextOptions<AppDbContext> TestOptions = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite("Data Source=unused.db")
        .UseSnakeCaseNamingConvention()
        .Options;

    private sealed class Shipment() : BaseEntity("shp")
    {
        public ShipmentDetail Detail { get; private set; } = null!;
    }

    private sealed class ShipmentDetail
    {
        public string ShipmentId { get; private set; } = null!;
        public string Notes { get; private set; } = null!;
    }

    private sealed class TableSplittingContext() : AppDbContext(TestOptions,
        TimeProvider.System, Substitute.For<ICurrentUserService>(), Substitute.For<ICallerIdentityResolver>(),
        Substitute.For<ITenantScopeProvider>(),
        new DomainEventDispatcher(new ServiceCollection().BuildServiceProvider()))
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.IgnoreTheAppEntities();
            modelBuilder.Entity<Shipment>(shipment =>
            {
                shipment.ToTable("shipments");
                shipment.HasKey(s => s.Id);
                shipment.HasOne(s => s.Detail).WithOne().HasForeignKey<ShipmentDetail>(d => d.ShipmentId);
                shipment.Navigation(s => s.Detail).IsRequired();
            });
            modelBuilder.Entity<ShipmentDetail>(detail =>
            {
                detail.ToTable("shipments");
                detail.HasKey(d => d.ShipmentId);
                detail.Property(d => d.ShipmentId).HasColumnName("id");
            });
        }
    }
}
