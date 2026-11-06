namespace Recall.Server.Databases;

/// <summary>
/// SQLite LIKE ignores the case of ASCII letters only.
/// </summary>
public static class LikePattern
{
    public const string EscapeCharacter = @"\";

    public static string Containing(string text) => $"%{Exactly(text)}%";

    public static string StartingWith(string text) => $"{Exactly(text)}%";

    public static string Exactly(string text)
        => text.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}
