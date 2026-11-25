namespace Recall.Server.Domain.ImportBatches;

using Exceptions;
using Sessions;
using Sessions.Content;

/// <summary>
/// What the person writes of an entry (IMP-6). A review sends it whole, and null clears an edit. <see cref="Words"/> is a
/// document, and it wins over the Raw of the agent at the accept (IMP-11).
/// </summary>
public sealed record ImportReview(bool Rejected, string? Words, TimeOnly? Time)
{
    public static readonly ImportReview None = new(false, null, null);

    /// <exception cref="ValidationException">The words are blank or over the limits of a Draft, or the time is not <c>HH:mm</c>.</exception>
    public static ImportReview Of(bool rejected, string? words, string? time)
    {
        if (words is not null)
        {
            DraftSave.CheckDocument(words, "words");
            ValidationException.MustNot(string.IsNullOrWhiteSpace(ProseMirrorToPlainText.Render(words)), "words",
                "Give the entry some words, or reject it to leave it out.");
        }

        return new ImportReview(rejected, words, ImportTimeOfDay.Parse(time, "time"));
    }
}
