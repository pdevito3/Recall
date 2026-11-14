namespace Recall.IntegrationTests.Domain.Recordings;

using Microsoft.EntityFrameworkCore;
using Recall.Server.Domain.Recordings;
using Recall.Server.Domain.Recordings.Features;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.Sessions.Features;
using Shouldly;

public class ChunkRevisionTests : TestBase
{
    private static readonly DateTimeOffset Typed = DateTimeOffset.UtcNow.AddMinutes(-1);

    [Test]
    [Property("Rule", "SES-48")]
    public async Task a_chunk_replaces_the_revision_of_the_chunk_before_it_and_each_other_revision_stays()
    {
        // Arrange
        var scope = new TestingServiceScope();
        var session = (await scope.SendAsync(new AddSession.Command(new SessionForCreationDto
        {
            ClientSessionId = Guid.NewGuid().ToString(),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        }))).Session;
        await scope.SendAsync(new SaveRawDraft.Command(session.Id, new DraftSaveDto { Text = "typed first", BaseRevision = 0, SavedAt = Typed }));
        var recording = Recording.Create(TestFixture.DefaultTenant.Id, TestFixture.DefaultUser.Id, session.Id, Guid.NewGuid().ToString(), RecordingFormat.Webm);
        await scope.InsertAsync(recording);
        Task<bool> ChunkAsync(long from, string words)
            => scope.SendAsync(new SaveTranscriptChunk.Command(recording.Id, TranscriptionEngineKind.Local, from, from + 100, from + 100, words));

        // Act
        await ChunkAsync(0, "chunk one");
        await ChunkAsync(100, "chunk two");
        var draft = await scope.SendAsync(new GetSession.Query(session.Id));
        await scope.SendAsync(new SaveRawDraft.Command(session.Id, new DraftSaveDto { Text = $"{draft.RawText} typed between", BaseRevision = draft.RawDraftRevision, SavedAt = Typed.AddSeconds(1) }));
        await scope.SendAsync(new SaveRawDraft.Command(session.Id, new DraftSaveDto { Text = "a save that lost", BaseRevision = 1, SavedAt = Typed.AddSeconds(2) }));
        await ChunkAsync(200, "chunk three");
        await ChunkAsync(300, "chunk four");

        // Assert
        var revisions = await scope.ExecuteDbContextAsync(db => db.RawRevisions.AsNoTracking()
            .Where(r => r.SessionId == session.Id)
            .OrderBy(r => r.Number)
            .Select(r => new { r.Number, r.Text, r.FromChunk })
            .ToListAsync());
        revisions.Select(r => (r.Number, r.FromChunk)).ShouldBe([(1, false), (3, true), (4, false), (5, false), (7, true)]);
        revisions[^1].Text.ShouldBe("typed first\n\nchunk one\n\nchunk two typed between\n\nchunk three\n\nchunk four");
        revisions.Single(r => r.Number == 5).Text.ShouldBe("a save that lost");
        var stored = await scope.SendAsync(new GetSession.Query(session.Id));
        stored.RawText.ShouldBe(revisions[^1].Text);
        stored.RawDraftRevision.ShouldBe(7);
    }
}
