namespace Recall.IntegrationTests.FeatureTests.People;

using Microsoft.EntityFrameworkCore;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.People.Features;
using Recall.Server.Domain.Sessions.Content;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Features;
using Recall.Server.Exceptions;
using Shouldly;

public class DeletePersonCommandTests : TestBase
{
    private static readonly DateTimeOffset Typed = DateTimeOffset.UtcNow.AddMinutes(-1);

    private static string Mentions(string firstId, string firstLabel, string secondId, string secondLabel)
        => "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Lunch with \"},"
            + "{\"type\":\"mention\",\"attrs\":{\"personId\":\"" + firstId + "\",\"label\":\"" + firstLabel + "\"}},"
            + "{\"type\":\"text\",\"text\":\" and \"},"
            + "{\"type\":\"mention\",\"attrs\":{\"personId\":\"" + secondId + "\",\"label\":\"" + secondLabel + "\"}}]}]}";

    [Test]
    [Property("Rule", "SES-47")]
    public async Task a_delete_turns_each_mention_into_plain_words_and_older_revisions_keep_the_mention()
    {
        // Arrange
        var scope = new TestingServiceScope();
        var sam = await scope.SendAsync(new AddPerson.Command(new PersonForCreationDto { Label = "Sam" }));
        var ana = await scope.SendAsync(new AddPerson.Command(new PersonForCreationDto { Label = "Ana" }));
        var session = (await scope.SendAsync(new AddSession.Command(new SessionForCreationDto
        {
            ClientSessionId = Guid.NewGuid().ToString(),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        }))).Session;
        var words = Mentions(sam.Id, "Sam", ana.Id, "Ana");
        await scope.SendAsync(new SaveRawDraft.Command(session.Id, new DraftSaveDto { Text = words, BaseRevision = 0, SavedAt = Typed }));
        await scope.SendAsync(new SaveCleanedDraft.Command(session.Id, new DraftSaveDto { Text = words, BaseRevision = 0, SavedAt = Typed }));

        // Act
        await scope.SendAsync(new DeletePerson.Command(sam.Id, null));

        // Assert
        var stored = await scope.SendAsync(new GetSession.Query(session.Id));
        stored.PersonIds.ShouldBe([ana.Id]);
        foreach (var text in new[] { stored.RawText, stored.CleanedText })
        {
            ProseMirrorToPlainText.Render(text).ShouldBe("Lunch with Sam and Ana");
            PersonMentions.PersonIdsIn(text).ShouldBe([ana.Id]);
            text.ShouldContain("{\"type\":\"text\",\"text\":\"Lunch with Sam and \"}");
        }

        stored.RawDraftRevision.ShouldBe(2);
        stored.CleanedDraftRevision.ShouldBe(2);
        (await scope.SendAsync(new GetRawRevision.Query(session.Id, 1))).Text.ShouldBe(words);
        await Should.ThrowAsync<NotFoundException>(() => scope.SendAsync(new GetPerson.Query(sam.Id)));
        var line = await scope.ExecuteDbContextAsync(db => db.SyncChanges.IgnoreQueryFilters().SingleAsync(c => c.EntityId == sam.Id));
        line.IsTombstone.ShouldBeTrue();
    }
}
