namespace Recall.Server.Domain.Sessions.Metadata;

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

        return new SessionMetadata(
            canonicalActivity,
            ListOf(ValuesOf(moods).Select(Mood.Canonical), "moods"),
            ListOf(ValuesOf(userTopics).Select(t => t.Trim()), "userTopics"));
    }

    private static IEnumerable<string> ValuesOf(IEnumerable<string?>? values)
        => (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!);

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
