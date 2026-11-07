namespace Recall.Server.Domain.Sessions.Cleanup;

using System.Text.Json;
using Databases;
using Metadata;

/// <summary>What a model or a device gives as the result of a Cleanup, after its checks.</summary>
public sealed class CleanupOutput
{
    private const int MaxSuggestions = 10;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private CleanupOutput(string cleanedMarkdown, string synopsis, IReadOnlyList<string> topics, IReadOnlyList<string> people, IReadOnlyList<string> moods)
    {
        CleanedMarkdown = cleanedMarkdown;
        Synopsis = synopsis;
        TopicSuggestions = topics;
        PersonNames = people;
        MoodSuggestions = moods;
    }

    public string CleanedMarkdown { get; }

    public string Synopsis { get; }

    public IReadOnlyList<string> TopicSuggestions { get; }

    public IReadOnlyList<string> PersonNames { get; }

    public IReadOnlyList<string> MoodSuggestions { get; }

    private sealed record Wire(string? CleanedMarkdown, string? Synopsis, string[]? TopicSuggestions, string[]? PeopleProposal, string[]? MoodSuggestions);

    /// <summary>
    /// Null when the words are not usable. A model can put words around the object, so this reads from the first brace to the
    /// last one. Each list loses each hidden character, and drops a value that the app cannot store, a value that is blank or too
    /// long, and a Mood outside the list of the app.
    /// </summary>
    public static CleanupOutput? Parse(string modelText)
    {
        var start = modelText.IndexOf('{');
        var end = modelText.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            return JsonSerializer.Deserialize<Wire>(modelText[start..(end + 1)], Web) is { } wire
                ? Checked(wire.CleanedMarkdown, wire.Synopsis, wire.TopicSuggestions, wire.PeopleProposal, wire.MoodSuggestions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static CleanupOutput? Checked(string? cleanedMarkdown, string? synopsis, IEnumerable<string?>? topics, IEnumerable<string?>? people, IEnumerable<string?>? moods)
    {
        if (cleanedMarkdown is null || string.IsNullOrWhiteSpace(cleanedMarkdown) || cleanedMarkdown.Length > Session.DraftMaxLength || !StorableText.CanStore(cleanedMarkdown))
            return null;

        var recap = VisibleText.Without(synopsis ?? string.Empty).Trim();
        if (recap.Length > Session.SynopsisMaxLength || !StorableText.CanStore(recap))
            return null;

        return new CleanupOutput(
            cleanedMarkdown,
            recap,
            Values(topics, Session.MetadataValueMaxLength),
            Values(people, People.Person.LabelMaxLength),
            Values(moods, Session.MetadataValueMaxLength).Where(Mood.IsKnown).Select(Mood.Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static List<string> Values(IEnumerable<string?>? values, int maxLength) => (values ?? [])
        .Where(value => value is not null && StorableText.CanStore(value))
        .Select(value => VisibleText.OneLine(value!))
        .Where(value => value.Length > 0 && value.Length <= maxLength)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaxSuggestions)
        .ToList();
}
