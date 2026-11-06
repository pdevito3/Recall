namespace Recall.UnitTests.Databases;

using Recall.Server.Databases;
using Recall.Server.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

public class VersionColumnTests
{
    [Test]
    public void every_entity_maps_its_version_to_an_integer_column_as_a_concurrency_token()
    {
        // Arrange
        using var context = new AppDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;
        var entityTypes = model.GetEntityTypes()
            .Where(e => e.ClrType.IsAssignableTo(typeof(BaseEntity)))
            .ToList();

        // Act
        var failures = entityTypes
            .Select(e => (Entity: e, Version: e.FindProperty(nameof(BaseEntity.Version))!))
            .Where(x => x.Version.GetColumnName() != "version"
                        || x.Version.GetColumnType() != "INTEGER"
                        || !x.Version.IsConcurrencyToken
                        || x.Version.ValueGenerated != ValueGenerated.Never)
            .Select(x => x.Entity.DisplayName())
            .ToList();

        // Assert
        entityTypes.ShouldNotBeEmpty();
        failures.ShouldBeEmpty();
    }
}
