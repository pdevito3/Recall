namespace Recall.UnitTests.Domain.ImportBatches;

using System.Globalization;
using Recall.Server.Domain.ImportBatches;
using Recall.Server.Exceptions;
using Shouldly;

public class ImportBatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Day = new(2020, 1, 1);
    private static readonly string[] ThreeIds = ["kept", "in-journal", "deleted"];

    private static ImportBatch Open(string name = "Day One", string zone = "America/Chicago") => ImportBatch.Open("ten_1", "usr_1", name, zone, Now);

    private static ImportEntry Entry(ImportBatch batch, string id, int index)
        => ImportEntry.Add(batch, id, ImportAgentLayer.Of(
            Day.AddDays(index / 720).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), index % 720, null, null, "Words", null, null));

    private static List<ImportEntry> ManyEntries(ImportBatch batch, int count)
        => Enumerable.Range(0, count).Select(i => Entry(batch, $"entry-{i}", i)).ToList();

    [Test]
    [Property("Rule", "IMP-1")]
    public void a_batch_opens_with_a_trimmed_name_and_a_zone()
    {
        // Act
        var batch = Open("  Day One  ");

        // Assert
        batch.Name.ShouldBe("Day One");
        batch.TimeZone.ShouldBe("America/Chicago");
        batch.State.ShouldBe(ImportBatchState.Open);
    }

    [Test]
    [Property("Rule", "IMP-1")]
    [Arguments("   ")]
    [Arguments("")]
    public void a_blank_name_is_refused(string name)
    {
        // Act
        var exception = Should.Throw<ValidationException>(() => Open(name));

        // Assert
        exception.Property.ShouldBe("name");
        exception.Message.ShouldBe("Give the batch a name of 1 to 200 characters.");
    }

    [Test]
    [Property("Rule", "IMP-1")]
    [Arguments("Factory")]
    [Arguments("Not/AZone")]
    public void a_zone_that_this_host_does_not_know_or_that_is_not_a_place_is_refused(string zone)
    {
        // Act
        var exception = Should.Throw<ValidationException>(() => Open(zone: zone));

        // Assert
        exception.Property.ShouldBe("timeZone");
        exception.Message.ShouldBe("This server does not know this time zone. Pick another zone.");
    }

    [Test]
    [Property("Rule", "IMP-27")]
    public void an_accept_of_more_than_20_000_kept_entries_is_refused_before_the_version_is_checked()
    {
        // Arrange
        var batch = Open();
        var entries = ManyEntries(batch, ImportBatch.AcceptMaxEntries + 1);

        // Act
        var exception = Should.Throw<ValidationException>(() => batch.Accept(batch.Version + 1, entries, new HashSet<string>(), new HashSet<string>(), Now));

        // Assert
        exception.Message.ShouldBe("This batch has more than 20,000 entries to accept. Split it into smaller batches, for example one for each year.");
        batch.State.ShouldBe(ImportBatchState.Open);
    }

    [Test]
    [Property("Rule", "IMP-27")]
    public void a_rejected_entry_does_not_count_toward_the_limit()
    {
        // Arrange
        var batch = Open();
        var entries = ManyEntries(batch, ImportBatch.AcceptMaxEntries + 1);
        batch.ReviewDay(Day, rejected: true);

        // Act
        var sessions = batch.Accept(batch.Version, entries, new HashSet<string>(), new HashSet<string>(), Now).Sessions;

        // Assert
        sessions.Count.ShouldBe(ImportBatch.AcceptMaxEntries + 1 - 720);
        batch.Result.EntriesRejected.ShouldBe(720);
        batch.Result.DaysRejected.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "IMP-17")]
    public void an_accept_from_another_version_is_refused_with_the_words_of_the_batch()
    {
        // Arrange
        var batch = Open();

        // Act
        var exception = Should.Throw<StaleVersionException>(() => batch.Accept(batch.Version + 1, [], new HashSet<string>(), new HashSet<string>(), Now));

        // Assert
        exception.Message.ShouldBe("This batch changed after you read it. Read it again, then accept it.");
    }

    [Test]
    [Property("Rule", "IMP-16")]
    public void an_entry_whose_id_became_a_session_or_got_a_tombstone_makes_no_session()
    {
        // Arrange
        var batch = Open();
        var entries = ThreeIds.Select((id, i) => Entry(batch, id, i)).ToList();

        // Act
        var accept = batch.Accept(batch.Version, entries, new HashSet<string> { "in-journal" }, new HashSet<string> { "deleted" }, Now);

        // Assert
        accept.Sessions.Select(s => s.ClientSessionId).ShouldBe(["kept"]);
        accept.Removed.ShouldBe(entries);
        batch.Result.ShouldBe(new ImportAcceptResult(1, 0, 0, 1, 1));
    }

    [Test]
    [Property("Rule", "IMP-14")]
    public void a_session_of_an_accept_holds_the_entry_and_names_its_batch()
    {
        // Arrange
        var batch = Open();
        var entry = ImportEntry.Add(batch, "lake", ImportAgentLayer.Of("2024-03-31", 1, "21:00", null, "At the *lake*", ["calm"], ["Lake"]));

        // Act
        var session = batch.Accept(batch.Version, [entry], new HashSet<string>(), new HashSet<string>(), Now).Sessions.Single();

        // Assert
        session.ClientSessionId.ShouldBe("lake");
        session.ImportBatchId.ShouldBe(batch.Id);
        session.TimeZone.ShouldBe("America/Chicago");
        session.StartedAt.ShouldBe(new DateTimeOffset(2024, 4, 1, 2, 0, 0, TimeSpan.Zero));
        session.Raw.Text.ShouldBe(entry.Agent.ToDocument());
        session.Raw.DraftRevision.ShouldBe(1);
        session.Moods.ShouldBe(["Calm"]);
        session.AiTopics.ShouldBe(["Lake"]);
        session.UserTopics.ShouldBeEmpty();
        session.CleanupStatus.ToString().ShouldBe("NotRun");
        batch.State.ShouldBe(ImportBatchState.Accepted);
    }
}
