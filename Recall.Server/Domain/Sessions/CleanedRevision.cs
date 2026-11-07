namespace Recall.Server.Domain.Sessions;

[NotAudited("The words of a person. A Revision is written once and goes with its Session, whose row has the version and the audit.")]
public class CleanedRevision
{
    public const int DeviceResultHashLength = 64;

    public string SessionId { get; private set; } = default!;

    public int Number { get; private set; }

    [Sensitive] public DateTimeOffset SavedAt { get; private set; }

    [Sensitive] public string Text { get; private set; } = default!;

    /// <summary>True when the Draft took these words at this number. False for a save that lost.</summary>
    public bool WasDraft { get; private set; }

    /// <summary>The hash of the device result that made this Revision. A device result applies once, and this finds a repeat.</summary>
    [Sensitive] public string? DeviceResultHash { get; private set; }

    public Session Session { get; private set; } = default!;

    internal CleanedRevision(string sessionId, int number, DateTimeOffset savedAt, string text, bool wasDraft, string? deviceResultHash = null)
    {
        SessionId = sessionId;
        Number = number;
        SavedAt = savedAt;
        Text = text;
        WasDraft = wasDraft;
        DeviceResultHash = deviceResultHash;
    }

    private CleanedRevision()
    {
    }
}
