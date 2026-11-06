namespace Recall.UnitTests.Databases;

using Recall.Server.Databases;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

public class UniqueIndexColumnsTests
{
    [Test]
    public void no_two_unique_indexes_of_a_table_share_their_columns()
    {
        // Arrange - a SQLite error names the table and the columns and not the index, so one column set maps to one index
        using var context = new AppDbContextFactory().CreateDbContext([]);
        var model = context.GetService<IDesignTimeModel>().Model;

        // Act
        var shared = model.GetEntityTypes()
            .SelectMany(e => e.GetIndexes().Where(i => i.IsUnique).Select(i =>
                $"{e.GetTableName()}({string.Join(',', i.Properties.Select(p => p.GetColumnName()))})"))
            .GroupBy(columns => columns)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        // Assert
        shared.ShouldBeEmpty();
    }
}
