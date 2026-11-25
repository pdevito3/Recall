namespace Recall.UnitTests.Domain.ImportBatches;

using Recall.Server.Domain.ImportBatches;
using Shouldly;

public class ImportPutOutcomeTests
{
    [Test]
    [Property("Rule", "IMP-28")]
    [Arguments(ImportIdentity.InJournal, true, true, true, ImportPutOutcome.InJournal)]
    [Arguments(ImportIdentity.Deleted, true, true, true, ImportPutOutcome.Deleted)]
    [Arguments(ImportIdentity.InOtherBatch, true, true, true, ImportPutOutcome.InOtherBatch)]
    [Arguments(ImportIdentity.InThisBatch, true, true, true, ImportPutOutcome.InTheFuture)]
    [Arguments(ImportIdentity.New, false, true, true, ImportPutOutcome.PositionTaken)]
    [Arguments(ImportIdentity.New, false, false, true, ImportPutOutcome.Rejected)]
    [Arguments(ImportIdentity.InThisBatch, false, false, true, ImportPutOutcome.Rejected)]
    [Arguments(ImportIdentity.New, false, false, false, ImportPutOutcome.Added)]
    public void an_entry_gets_the_first_outcome_that_applies(
        ImportIdentity identity, bool inTheFuture, bool positionTaken, bool rejected, ImportPutOutcome expected)
    {
        // Act
        var outcome = ImportPutOutcomes.Of(identity, inTheFuture, positionTaken, rejected, unchanged: false);

        // Assert
        outcome.ShouldBe(expected);
    }

    [Test]
    [Property("Rule", "IMP-28")]
    [Arguments(true, ImportPutOutcome.Unchanged)]
    [Arguments(false, ImportPutOutcome.Changed)]
    public void an_entry_of_the_batch_is_unchanged_or_changed(bool unchanged, ImportPutOutcome expected)
    {
        // Act
        var outcome = ImportPutOutcomes.Of(ImportIdentity.InThisBatch, false, false, false, unchanged);

        // Assert
        outcome.ShouldBe(expected);
    }

    [Test]
    [Property("Rule", "IMP-28")]
    public void only_rejected_changed_and_added_store_the_agent_layer()
    {
        // Act
        var storing = Enum.GetValues<ImportPutOutcome>().Where(outcome => outcome.Stores());

        // Assert
        storing.ShouldBe([ImportPutOutcome.Rejected, ImportPutOutcome.Changed, ImportPutOutcome.Added]);
    }
}
