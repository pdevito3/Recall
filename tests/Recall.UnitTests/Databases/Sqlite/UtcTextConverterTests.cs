namespace Recall.UnitTests.Databases.Sqlite;

using Recall.Server.Databases.Sqlite;
using Shouldly;

public class UtcTextConverterTests
{
    [Test]
    public void a_value_with_an_offset_stores_as_its_utc_instant()
    {
        // Arrange
        var value = new DateTimeOffset(2026, 3, 8, 1, 30, 15, 250, TimeSpan.FromHours(-5));

        // Act
        var text = UtcTextConverter.ToText(value);

        // Assert
        text.ShouldBe("2026-03-08T06:30:15.250000Z");
    }

    [Test]
    public void text_reads_back_as_the_same_instant_with_a_zero_offset()
    {
        // Arrange
        var value = new DateTimeOffset(2026, 3, 8, 1, 30, 15, 250, TimeSpan.FromHours(9));

        // Act
        var read = UtcTextConverter.FromText(UtcTextConverter.ToText(value));

        // Assert
        read.ShouldBe(value);
        read.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Test]
    public void each_value_has_the_same_width_and_the_text_order_is_the_order_of_the_instants()
    {
        // Arrange
        DateTimeOffset[] instants =
        [
            DateTimeOffset.MinValue,
            new(1999, 12, 31, 23, 59, 59, 999, TimeSpan.Zero),
            new(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(2)),
            new(2026, 1, 1, 8, 0, 0, 1, TimeSpan.Zero),
            new(2026, 1, 1, 4, 0, 1, TimeSpan.FromHours(-4)),
            new(2026, 1, 1, 13, 30, 2, TimeSpan.FromHours(5.5)),
            DateTimeOffset.MaxValue,
        ];

        // Act
        var texts = instants.Select(UtcTextConverter.ToText).ToList();

        // Assert
        texts.Select(t => t.Length).Distinct().ShouldHaveSingleItem();
        texts.OrderBy(t => t, StringComparer.Ordinal).ShouldBe(texts);
    }
}
