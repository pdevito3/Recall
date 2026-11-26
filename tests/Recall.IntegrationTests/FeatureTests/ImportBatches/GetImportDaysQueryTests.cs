namespace Recall.IntegrationTests.FeatureTests.ImportBatches;

using Recall.Server.Domain.ImportBatches.Dtos;
using Recall.Server.Domain.ImportBatches.Features;
using Recall.Server.Domain.Sessions.Content;
using Recall.Server.Exceptions;
using Recall.SharedTestHelpers.EntityGraphs;
using Shouldly;
using static ImportScenario;

public class GetImportDaysQueryTests : TestBase
{
    private static readonly DateOnly First = new(2024, 3, 1);
    private static readonly DateOnly Second = new(2024, 3, 2);
    private static readonly DateOnly Third = new(2024, 3, 3);
    private static readonly DateOnly Empty = new(2024, 3, 4);

    [Test]
    [Property("Rule", "IMP-31")]
    public async Task each_date_of_a_batch_comes_in_order_with_its_entries_its_own_rejects_and_the_reject_of_its_day()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id,
            Entry("third", 0, date: Third),
            Entry("first-a", 0, date: First),
            Entry("first-b", 1, date: First),
            Entry("first-c", 2, date: First),
            Entry("second-a", 0, date: Second),
            Entry("second-b", 1, date: Second));
        await RejectAsync(owner, batch.Id, "first-b");
        await RejectAsync(owner, batch.Id, "second-a");
        await RejectDayAsync(owner, batch.Id, Second);
        await RejectDayAsync(owner, batch.Id, Empty);
        await RejectDayAsync(owner, batch.Id, Third);
        var last = await RejectDayAsync(owner, batch.Id, Third, rejected: false);

        // Act
        var days = await WebSessionOf(owner).SendAsync(new GetImportDays.Query(batch.Id));

        // Assert
        days.Version.ShouldBe(last.Version);
        days.Days.ShouldBe([
            new ImportDayDto { Date = First, EntryCount = 3, RejectedCount = 1, Rejected = false },
            new ImportDayDto { Date = Second, EntryCount = 2, RejectedCount = 1, Rejected = true },
            new ImportDayDto { Date = Third, EntryCount = 1, RejectedCount = 0, Rejected = false },
            new ImportDayDto { Date = Empty, EntryCount = 0, RejectedCount = 0, Rejected = true },
        ]);
    }

    [Test]
    [Property("Rule", "IMP-31")]
    public async Task a_batch_of_another_user_gets_404()
    {
        // Arrange
        await new TestingServiceScope().StartGraph(out _)
            .AddTenant(out var tenant)
            .AddUser(out var owner).Under(tenant)
            .AddUser(out var other).Under(tenant)
            .BuildAsync();
        var batch = await OpenAsync(owner);
        await PutAsync(owner, batch.Id, Entry("a"));

        // Act
        var read = () => WebSessionOf(other).SendAsync(new GetImportDays.Query(batch.Id));

        // Assert
        var refused = await read.ShouldThrowAsync<NotFoundException>();
        refused.Message.ShouldBe("This import batch does not exist.");
    }

    [Test]
    [Property("Rule", "IMP-32")]
    public async Task an_entry_gives_the_document_that_the_accept_stores_raw_as_a_document_until_the_person_edits_the_words()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var batch = await OpenAsync(owner);
        const string raw = "A **bold** day\n\n- one\n- two";
        await PutAsync(owner, batch.Id, Entry("a", raw: raw));
        const string edit = """{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"my words"}]}]}""";
        var before = await EntriesAsync(owner, batch.Id);

        // Act
        var edited = await WebSessionOf(owner).SendAsync(new ReviewImportEntry.Command(batch.Id,
            new ImportEntryReviewDto { ClientSessionId = "a", Rejected = false, Words = edit }));

        // Assert
        before.Entries.Single().Document.ShouldBe(MarkdownToProseMirror.ConvertToJson(raw));
        before.Entries.Single().Document.ShouldContain("\"bold\"");
        edited.Entry.Document.ShouldBe(edit);
        (await EntriesAsync(owner, batch.Id)).Entries.Single().Document.ShouldBe(edit);
    }
}
