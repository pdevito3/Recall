namespace Recall.Server.Domain.ModelProviders.Clients;

using System.Globalization;
using System.Text;

/// <summary>
/// The characters that a person can see. A control character can break a line of a page, and a bidi control can show a word
/// in another order than it has. A line end stays only in a message, and the replacement character means that the bytes were bad.
/// </summary>
public static class VisibleText
{
    private const char Replacement = (char)0xFFFD;
    private const char ZeroWidthJoiner = (char)0x200D; // Part of an emoji, and of some words.
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    public static string Without(string value)
        => new(value.Where(c => !IsHidden(c) || c == '\n').ToArray());

    /// <summary>The text with no hidden character, and its runs of white space as one space.</summary>
    public static string OneLine(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value.Where(c => !IsHidden(c) || char.IsWhiteSpace(c)))
        {
            if (!char.IsWhiteSpace(c))
                result.Append(c);
            else if (result.Length > 0 && result[^1] != ' ')
                result.Append(' ');
        }

        return result.ToString().TrimEnd();
    }

    public static bool IsClean(string value) => !value.Any(IsHidden) && !value.Contains(Replacement);

    private static bool IsHidden(char c)
        => char.IsControl(c)
            || c is LineSeparator or ParagraphSeparator
            || (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format && c != ZeroWidthJoiner);
}
