namespace Recall.Server.Domain.Sessions;

using Content;
using Exceptions;

public sealed class DraftSave
{
    private const string TextField = "text";
    private const string BaseRevisionField = "baseRevision";

    private DraftSave(string text, int baseRevision, DateTimeOffset claimedSavedAt, DateTimeOffset savedAt)
    {
        Text = text;
        BaseRevision = baseRevision;
        ClaimedSavedAt = claimedSavedAt;
        SavedAt = savedAt;
    }

    public string Text { get; }

    public int BaseRevision { get; }

    /// <summary>The time that the client sent. A replay sends it again, also when the clock of the client is fast.</summary>
    public DateTimeOffset ClaimedSavedAt { get; }

    public DateTimeOffset SavedAt { get; }

    public static DraftSave Of(string text, int baseRevision, DateTimeOffset claimedSavedAt, DateTimeOffset now)
    {
        ValidationException.ThrowWhenLongerThan(text, Session.DraftMaxStoredLength, TextField,
            "The text has too much formatting to save. Remove some formatting or split the text.");
        ValidationException.ThrowWhenLongerThan(ProseMirrorToPlainText.Render(text), Session.DraftMaxLength, TextField,
            $"The text must not have more than {Session.DraftMaxLength:N0} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(text, TextField, "The text has a character that is not allowed.");

        ValidationException.Must(baseRevision >= DraftSlot.NoRevision, BaseRevisionField, "The base revision must not be negative.");

        return new DraftSave(text, baseRevision, claimedSavedAt, claimedSavedAt < now ? claimedSavedAt : now);
    }
}
