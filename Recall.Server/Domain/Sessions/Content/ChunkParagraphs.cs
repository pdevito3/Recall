namespace Recall.Server.Domain.Sessions.Content;

/// <summary>
/// What one transcript chunk adds to Raw (REC-27). Each paragraph holds whole words of the chunk, in their order, and none is
/// empty, so a break falls only between two words. With <see cref="JoinsLastParagraph"/> the first paragraph goes on at the
/// end of the last paragraph of Raw, after a space. Each other paragraph starts a paragraph.
/// </summary>
public sealed record ChunkParagraphs
{
    public ChunkParagraphs(bool joinsLastParagraph, IReadOnlyList<string> paragraphs)
    {
        if (paragraphs.Count == 0 || paragraphs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A chunk adds at least one paragraph, and each has words.", nameof(paragraphs));

        JoinsLastParagraph = joinsLastParagraph;
        Paragraphs = paragraphs;
    }

    public bool JoinsLastParagraph { get; }

    public IReadOnlyList<string> Paragraphs { get; }

    /// <summary>The chunk of a Recording with no taps: its own paragraph.</summary>
    public static ChunkParagraphs Own(string words) => new(false, [words]);
}
