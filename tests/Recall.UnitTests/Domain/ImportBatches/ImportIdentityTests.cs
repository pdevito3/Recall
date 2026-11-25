namespace Recall.UnitTests.Domain.ImportBatches;

using Recall.Server.Domain.ImportBatches;
using Shouldly;

public class ImportIdentityTests
{
    [Test]
    [Property("Rule", "IMP-4")]
    [Arguments(true, false, null, ImportIdentity.InJournal)]
    [Arguments(true, false, "imp_this", ImportIdentity.InJournal)]
    [Arguments(false, true, null, ImportIdentity.Deleted)]
    [Arguments(false, true, "imp_this", ImportIdentity.Deleted)]
    [Arguments(false, false, "imp_other", ImportIdentity.InOtherBatch)]
    [Arguments(false, false, "imp_this", ImportIdentity.InThisBatch)]
    [Arguments(false, false, null, ImportIdentity.New)]
    public void an_id_is_a_session_then_a_tombstone_then_an_entry_of_a_batch(
        bool isSession, bool hasTombstone, string? batchOfEntry, ImportIdentity expected)
    {
        // Act
        var identity = ImportIdentities.Classify(isSession, hasTombstone, batchOfEntry, "imp_this");

        // Assert
        identity.ShouldBe(expected);
    }
}
