namespace Recall.Server.Domain.Sessions;

using Content;
using Exceptions;

public sealed class DraftSave
{
    public const int ChangeIdMaxLength = 64;

    private const string TextField = "text";
    private const string BaseRevisionField = "baseRevision";
    private const string ChangeIdField = "changeId";
    private const string ParentChangeIdField = "parentChangeId";

    private DraftSave(string text, int baseRevision, DateTimeOffset savedAt, string? changeId, string? parentChangeId)
    {
        Text = text;
        BaseRevision = baseRevision;
        SavedAt = savedAt;
        ChangeId = changeId;
        ParentChangeId = parentChangeId;
    }

    public string Text { get; }

    public int BaseRevision { get; }

    public DateTimeOffset SavedAt { get; }

    /// <summary>The ID that the client gave these words. It decides no save. SES-46.</summary>
    public string? ChangeId { get; }

    /// <summary>The ID of the change that these words grew from. It decides no save. SES-46.</summary>
    public string? ParentChangeId { get; }

    public static DraftSave Of(string text, int baseRevision, DateTimeOffset savedAt, string? changeId = null, string? parentChangeId = null)
    {
        ValidationException.ThrowWhenLongerThan(text, Session.DraftMaxStoredLength, TextField,
            "The text is too long or has too much formatting to save. Make the text shorter or split the text.");
        ValidationException.ThrowWhenLongerThan(ProseMirrorToPlainText.Render(text), Session.DraftMaxLength, TextField,
            $"The text must not have more than {Session.DraftMaxLength:N0} characters, and each line break counts.");
        ValidationException.ThrowWhenHasCharacterNotStorable(text, TextField, "The text has a character that is not allowed.");
        ValidationException.Must(!Content.LinkAddress.AnyRefusedIn(text), TextField, "The text has a link that no page may open.");

        ValidationException.Must(baseRevision >= DraftSlot.NoRevision, BaseRevisionField, "The base revision must not be negative.");

        MustBeAChangeId(changeId, ChangeIdField, "The change ID");
        MustBeAChangeId(parentChangeId, ParentChangeIdField, "The parent change ID");

        return new DraftSave(text, baseRevision, savedAt, changeId, parentChangeId);
    }

    private static void MustBeAChangeId(string? id, string field, string name)
    {
        if (id is null)
            return;

        ValidationException.Must(id.Length > 0, field, $"{name} must not be empty.");
        ValidationException.ThrowWhenLongerThan(id, ChangeIdMaxLength, field, $"{name} must not have more than {ChangeIdMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(id, field, $"{name} has a character that is not allowed.");
    }
}
