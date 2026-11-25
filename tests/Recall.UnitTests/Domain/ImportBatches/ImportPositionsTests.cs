namespace Recall.UnitTests.Domain.ImportBatches;

using Recall.Server.Domain.ImportBatches;
using Shouldly;

public class ImportPositionsTests
{
    private static readonly DateOnly Day = new(2024, 3, 31);

    private static ImportSlot At(int position) => new(Day, position);

    [Test]
    [Property("Rule", "IMP-8")]
    public void a_call_can_swap_two_positions()
    {
        // Arrange
        var stored = new Dictionary<string, ImportSlot> { ["a"] = At(0), ["b"] = At(1) };
        var proposed = new Dictionary<string, ImportSlot> { ["a"] = At(1), ["b"] = At(0) };

        // Act
        var taken = ImportPositions.Check(stored, proposed);

        // Assert
        taken.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "IMP-8")]
    public void two_entries_of_a_call_on_one_position_each_name_the_other()
    {
        // Arrange
        var proposed = new Dictionary<string, ImportSlot> { ["a"] = At(3), ["b"] = At(3) };

        // Act
        var taken = ImportPositions.Check(new Dictionary<string, ImportSlot>(), proposed);

        // Assert
        taken.ShouldBe(new Dictionary<string, string> { ["a"] = "b", ["b"] = "a" });
    }

    [Test]
    [Property("Rule", "IMP-8")]
    public void a_stored_entry_that_the_call_does_not_name_keeps_its_position()
    {
        // Arrange
        var stored = new Dictionary<string, ImportSlot> { ["kept"] = At(0) };
        var proposed = new Dictionary<string, ImportSlot> { ["new"] = At(0) };

        // Act
        var taken = ImportPositions.Check(stored, proposed);

        // Assert
        taken.ShouldBe(new Dictionary<string, string> { ["new"] = "kept" });
    }

    [Test]
    [Property("Rule", "IMP-8")]
    public void the_same_position_on_another_date_is_free()
    {
        // Arrange
        var stored = new Dictionary<string, ImportSlot> { ["a"] = At(0) };
        var proposed = new Dictionary<string, ImportSlot> { ["b"] = new(Day.AddDays(1), 0) };

        // Act
        var taken = ImportPositions.Check(stored, proposed);

        // Assert
        taken.ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "IMP-8")]
    public void a_refused_entry_goes_back_to_its_stored_position_and_the_check_repeats()
    {
        // Arrange
        var stored = new Dictionary<string, ImportSlot> { ["x"] = At(0), ["y"] = At(1) };
        var proposed = new Dictionary<string, ImportSlot> { ["x"] = At(1), ["y"] = At(2), ["z"] = At(2) };

        // Act
        var taken = ImportPositions.Check(stored, proposed);

        // Assert
        taken.ShouldBe(new Dictionary<string, string> { ["y"] = "z", ["z"] = "y", ["x"] = "y" });
    }
}
