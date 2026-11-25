namespace Recall.Server.Domain.Sessions;

using Content;
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

    public DateTimeOffset SavedAt { get; }

    public static DraftSave Of(string text, int baseRevision, DateTimeOffset savedAt)
    {
        CheckDocument(text, TextField);
        ValidationException.Must(baseRevision >= DraftSlot.NoRevision, BaseRevisionField, "The base revision must not be negative.");

        return new DraftSave(text, baseRevision, savedAt);
    }

    public static void CheckDocument(string text, string field)
    {
        ValidationException.ThrowWhenLongerThan(text, Session.DraftMaxStoredLength, field,
            "The text is too long or has too much formatting to save. Make the text shorter or split the text.");
        ValidationException.ThrowWhenLongerThan(ProseMirrorToPlainText.Render(text), Session.DraftMaxLength, field,
            $"The text must not have more than {Session.DraftMaxLength:N0} characters, and each line break counts.");
        ValidationException.ThrowWhenHasCharacterNotStorable(text, field, "The text has a character that is not allowed.");
        ValidationException.Must(!Content.LinkAddress.AnyRefusedIn(text), field, "The text has a link that no page may open.");
    }
}
