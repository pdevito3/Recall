namespace Recall.Server.Resources;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Reads a time from a client only when it names its offset. The default reads a time with no offset in the local time of the server.</summary>
public sealed class InstantWithOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var instant = reader.GetDateTimeOffset();
        var text = reader.GetString()!;
        var time = text.IndexOf('T');
        if (time < 0 || text.AsSpan(time).IndexOfAny('Z', '+', '-') < 0)
            throw new JsonException("A time must name its offset, such as 2026-01-01T00:00:00Z.");

        return instant;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
