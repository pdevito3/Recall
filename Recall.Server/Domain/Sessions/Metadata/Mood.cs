namespace Recall.Server.Domain.Sessions.Metadata;

using System.Collections.Frozen;

public static class Mood
{
    private static readonly FrozenDictionary<string, string> Known = new[]
    {
        "Joyful", "Content", "Calm", "Neutral", "Tired", "Anxious", "Sad", "Angry", "Excited", "Grateful",
    }.ToFrozenDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);

    public static string Canonical(string value)
    {
        var trimmed = value.Trim();
        return Known.GetValueOrDefault(trimmed, trimmed);
    }
}
