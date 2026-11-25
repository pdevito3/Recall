namespace Recall.Server.Domain.ImportBatches;

using Exceptions;
using Sessions;
using Sessions.Content;

public sealed record ImportReview(bool Rejected, string? Words, TimeOnly? Time)
{
    public static readonly ImportReview None = new(false, null, null);

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
