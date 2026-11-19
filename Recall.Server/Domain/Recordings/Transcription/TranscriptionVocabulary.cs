namespace Recall.Server.Domain.Recordings.Transcription;

using System.Text;

/// <summary>
/// The <c>prompt</c> of a cloud transcription: the terms of the Corrections of a person, spelled right, as the comma-separated
/// list that the OpenAI guide shows. A mishearing never goes in, because the engine would learn its spelling.
/// </summary>
public static class TranscriptionVocabulary
{
    /// <summary>
    /// Whisper and Groq take at most 224 tokens. A token of their byte-level tokenizers holds at least one byte, so 224 UTF-8
    /// bytes are never more than 224 tokens, whatever the language.
    /// </summary>
    public const int MaxBytes = 224;

    private const string Separator = ", ";

    /// <summary>A term of a Correction and the time of the last save of its row.</summary>
    public sealed record KnownWord(string Term, DateTimeOffset ChangedOn);

    /// <summary><paramref name="Text"/> is null when no term fits, so the call sends no <c>prompt</c>.</summary>
    public sealed record Prompt(string? Text, int Terms, int Sent, int Bytes);

    /// <summary>
    /// The newest terms go first until the list is full. A term that does not fit is left out, and an older shorter one can
    /// still go. The terms that go are in the order of their spelling, so the same terms always give the same prompt.
    /// </summary>
    public static Prompt PromptOf(IEnumerable<KnownWord> words)
    {
        var distinct = words
            .Select(w => w with { Term = w.Term.Trim() })
            .Where(w => w.Term.Length > 0)
            .OrderByDescending(w => w.ChangedOn)
            .ThenBy(w => w.Term, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Term, StringComparer.Ordinal)
            .Select(w => w.Term)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var chosen = new List<string>();
        var bytes = 0;
        foreach (var term in distinct)
        {
            var added = Encoding.UTF8.GetByteCount(term) + (chosen.Count > 0 ? Separator.Length : 0);
            if (bytes + added > MaxBytes)
                continue;

            chosen.Add(term);
            bytes += added;
        }

        var text = chosen.Count > 0
            ? string.Join(Separator, chosen.Order(StringComparer.OrdinalIgnoreCase).ThenBy(t => t, StringComparer.Ordinal))
            : null;
        return new Prompt(text, distinct.Count, chosen.Count, bytes);
    }
}
