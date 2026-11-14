namespace Recall.Server.Domain.Sessions;

[NotAudited("The words of a person. A Revision is written once and goes with its Session, whose row has the version and the audit.")]
public class RawRevision
{
    public string SessionId { get; private set; } = default!;

    public int Number { get; private set; }

    [Sensitive] public DateTimeOffset SavedAt { get; private set; }

    [Sensitive] public string Text { get; private set; } = default!;

    /// <summary>True when the Draft took these words at this number. False for a save that lost.</summary>
    public bool WasDraft { get; private set; }

    /// <summary>True when a chunk of a Recording made it. The next chunk holds each of its words, so that chunk replaces it (SES-48).</summary>
    public bool FromChunk { get; private set; }

    public Session Session { get; private set; } = default!;

    internal RawRevision(string sessionId, int number, DateTimeOffset savedAt, string text, bool wasDraft, bool fromChunk = false)
    {
        SessionId = sessionId;
        Number = number;
        SavedAt = savedAt;
        Text = text;
        WasDraft = wasDraft;
        FromChunk = fromChunk;
    }

    private RawRevision()
    {
    }
}
