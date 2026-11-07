namespace Recall.Server.Domain.Summaries;

using System.Globalization;
using System.Text;

/// <summary>One source of a Summary: the words of a Session, or the words of a source Summary.</summary>
public sealed record SummarySourceText(DateOnly Day, string Text);

/// <summary>The request of a Summary to a model provider. The words of each source go in the text, and none in the instructions.</summary>
public static class SummaryPrompt
{
    public const string Instructions = """
        You are a reflective journaling assistant. You write a short narrative of one person's journal over a period.

        Length: a few sentences for a day, and up to a couple of short paragraphs for a longer period.

        Rules:
        - Capture the throughline: the themes, the mood, and how they shift over the period.
        - Write in the third person, as "they", in a warm and plain voice.
        - Synthesize. Do not restate each entry in order.
        - Invent nothing. Use only what the entries say.
        - Respond with only the narrative prose: no preamble, no headings, no bullet lists, and no quotes.
        """;

    public static string TextOf(PeriodKey key, IReadOnlyList<SummarySourceText> sources)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"<summary-sources period=\"{key.Period}\" first-day=\"{key.Start:yyyy-MM-dd}\" last-day=\"{key.Last:yyyy-MM-dd}\">\n");
        var kind = key.SourcePeriod is { } source ? $"{source} summary" : "Entry";
        for (var index = 0; index < sources.Count; index++)
            text.Append(CultureInfo.InvariantCulture, $"<source n=\"{index + 1}\" kind=\"{kind}\" day=\"{sources[index].Day:yyyy-MM-dd}\">\n{sources[index].Text}\n</source>\n");
        text.Append("</summary-sources>");
        return text.ToString();
    }

    /// <summary>The words to store, or null when the answer is no Summary: empty, or longer than <see cref="Summary.ContentMaxLength"/>.</summary>
    public static string? ContentOf(string answer)
    {
        var content = answer.Trim();
        return content.Length is 0 or > Summary.ContentMaxLength ? null : content;
    }
}
