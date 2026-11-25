namespace Recall.Server.Domain.Sessions.Metadata;

using Databases;
using Exceptions;

public sealed record SessionMetadata
{
    private SessionMetadata(string activity, List<string> moods, List<string> userTopics)
    {
        Activity = activity;
        Moods = moods;
        UserTopics = userTopics;
    }

    public string Activity { get; }

    public List<string> Moods { get; }

    public List<string> UserTopics { get; }

    public static SessionMetadata Of(string? activity, IEnumerable<string?>? moods, IEnumerable<string?>? userTopics)
    {
        var canonicalActivity = SessionActivity.Canonical(activity);
        CheckValue(canonicalActivity, "activity");

        return new SessionMetadata(canonicalActivity, MoodsOf(moods, "moods"), TopicsOf(userTopics, "userTopics"));
    }

    public static List<string> MoodsOf(IEnumerable<string?>? moods, string field)
        => ListOf(VisibleValuesOf(moods, field).Select(Mood.Canonical), field);

    public static List<string> TopicsOf(IEnumerable<string?>? topics, string field)
        => ListOf(VisibleValuesOf(topics, field), field);

    /// <summary>A character that the store refuses stays refused. The other hidden characters go, so a page shows what the value holds.</summary>
    private static IEnumerable<string> VisibleValuesOf(IEnumerable<string?>? values, string field)
    {
        foreach (var value in (values ?? []).Where(value => value is not null))
        {
            ValidationException.ThrowWhenHasCharacterNotStorable(value!, field, $"The {field} value has a character that is not allowed.");
            if (VisibleText.OneLine(value!) is { Length: > 0 } visible)
                yield return visible;
        }
    }

    private static List<string> ListOf(IEnumerable<string> values, string field)
    {
        var list = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ValidationException.Must(list.Count <= Session.MetadataListMaxCount, field,
            $"The {field} list must not have more than {Session.MetadataListMaxCount} values.");
        list.ForEach(value => CheckValue(value, field));
        return list;
    }

    private static void CheckValue(string value, string field)
    {
        ValidationException.ThrowWhenLongerThan(value, Session.MetadataValueMaxLength, field,
            $"Each {field} value must not have more than {Session.MetadataValueMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(value, field, $"The {field} value has a character that is not allowed.");
    }
}
