namespace Recall.Server.Domain.ImportBatches;

using System.Globalization;
using Exceptions;
using Sessions;
using Sessions.Content;
using Sessions.Metadata;

/// <summary>
/// What the agent writes of an entry (IMP-5). A put replaces it whole, and no route of the agent writes the review.
/// <see cref="Raw"/> is the Markdown of the source, which the accept turns into a document (IMP-11).
/// </summary>
public sealed record ImportAgentLayer
{
    public const string DateFormat = "yyyy-MM-dd";
    public const int DateTextLength = 10;

    /// <summary>A day holds positions 0 to 719, so the start of the last one, 12:00 plus 719 minutes, is on the same day (IMP-7).</summary>
    public const int PositionMax = 719;

    public const int RawMaxLength = Session.DraftMaxLength;

    private ImportAgentLayer()
    {
    }

    public DateOnly Date { get; private init; }

    public int Position { get; private init; }

    public TimeOnly? Time { get; private init; }

    public string? TimeZone { get; private init; }

    public string Raw { get; private init; } = default!;

    public IReadOnlyList<string> Moods { get; private init; } = [];

    public IReadOnlyList<string> Topics { get; private init; } = [];

    /// <exception cref="ValidationException">A value is not valid. The property names its request field (IMP-28).</exception>
    public static ImportAgentLayer Of(
        string date, int position, string? time, string? timeZone, string raw, IEnumerable<string?>? moods, IEnumerable<string?>? topics)
    {
        if (!DateOnly.TryParseExact(date, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            throw new ValidationException("date", "Give the date as YYYY-MM-DD, for example 2024-03-31.");
        ValidationException.Must(position is >= 0 and <= PositionMax, "position", $"The position must be from 0 to {PositionMax}.");

        return new ImportAgentLayer
        {
            Date = day,
            Position = position,
            Time = ImportTimeOfDay.Parse(time, "time"),
            TimeZone = timeZone is null ? null : ImportZone.Parse(timeZone, "timeZone").Value,
            Raw = RawOf(raw),
            Moods = SessionMetadata.MoodsOf(moods, "moods"),
            Topics = SessionMetadata.TopicsOf(topics, "topics"),
        };
    }

    /// <summary>The document that the accept stores as Raw. The conversion keeps each word (IMP-11).</summary>
    public string ToDocument() => MarkdownToProseMirror.ConvertToJson(Raw);

    public bool Equals(ImportAgentLayer? other)
        => other is not null && Date == other.Date && Position == other.Position && Time == other.Time && TimeZone == other.TimeZone
           && Raw == other.Raw && Moods.SequenceEqual(other.Moods) && Topics.SequenceEqual(other.Topics);

    public override int GetHashCode() => HashCode.Combine(Date, Position, Time, TimeZone, Raw);

    private static string RawOf(string raw)
    {
        const string field = "raw";
        ValidationException.ThrowWhenNullOrWhitespace(raw, field, "Give the entry the words of its source.");
        ValidationException.ThrowWhenLongerThan(raw, RawMaxLength, field, $"The raw text must not have more than {RawMaxLength:N0} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(raw, field, "The raw text has a character that is not allowed.");

        var document = MarkdownToProseMirror.ConvertToJson(raw);
        DraftSave.CheckDocument(document, field);
        ValidationException.MustNot(string.IsNullOrWhiteSpace(ProseMirrorToPlainText.Render(document)), field,
            "Give the entry the words of its source. A mark of the form alone has no words.");
        return raw;
    }
}
