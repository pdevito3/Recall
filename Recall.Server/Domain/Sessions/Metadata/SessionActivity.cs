namespace Recall.Server.Domain.Sessions.Metadata;

using System.Collections.Frozen;

public static class SessionActivity
{
    public const string None = nameof(None);

    private static readonly FrozenDictionary<string, string> Known = new[]
    {
        None, "Stationary", "Walking", "Eating", "Commuting", "Exercising", "Resting",
    }.ToFrozenDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);

    public static string Canonical(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return None;

        var trimmed = value.Trim();
        return Known.GetValueOrDefault(trimmed, trimmed);
    }
}
