namespace Recall.Server.Domain.Sessions;

using Exceptions;

public sealed class DraftSave
{
    private const string TextField = "text";
    private const string BaseRevisionField = "baseRevision";

    private DraftSave(string text, int baseRevision, DateTimeOffset savedAt)
    {
        Text = text;
        BaseRevision = baseRevision;
        SavedAt = savedAt;
    }

    public string Text { get; }

    public int BaseRevision { get; }

    /// <summary>The time that the client sent, in UTC. With the words, it says that a save is a replay. SES-8.</summary>
    public DateTimeOffset SavedAt { get; }

    public static DraftSave Of(string text, int baseRevision, DateTimeOffset savedAt)
    {
        ValidationException.ThrowWhenLongerThan(text, Session.DraftMaxLength, TextField,
            $"The text must not have more than {Session.DraftMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(text, TextField, "The text has a character that is not allowed.");

        ValidationException.Must(baseRevision >= DraftSlot.NoRevision, BaseRevisionField, "The base revision must not be negative.");

        return new DraftSave(text, baseRevision, savedAt.ToUniversalTime());
    }
}
