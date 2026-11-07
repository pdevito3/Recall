namespace Recall.Server.Domain.Sessions;

[NotAudited("The words of a person. A Revision is written once and goes with its Session, whose row has the version and the audit.")]
public class CleanedRevision
{
    public string SessionId { get; private set; } = default!;

    public int Number { get; private set; }

    [Sensitive] public DateTimeOffset SavedAt { get; private set; }

    [Sensitive] public DateTimeOffset ClaimedSavedAt { get; private set; }

    [Sensitive] public string Text { get; private set; } = default!;

    /// <summary>True when the Draft took these words at this number. False for a save that lost, and for each row from before this column.</summary>
    public bool WasDraft { get; private set; }

    public Session Session { get; private set; } = default!;

    internal CleanedRevision(string sessionId, int number, DateTimeOffset savedAt, DateTimeOffset claimedSavedAt, string text, bool wasDraft)
    {
        SessionId = sessionId;
        Number = number;
        SavedAt = savedAt;
        ClaimedSavedAt = claimedSavedAt;
        Text = text;
        WasDraft = wasDraft;
    }

    private CleanedRevision()
    {
    }
}
