namespace Recall.Server.Resources.FileTelemetry;

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

public static class JsonLine
{
    public static string Of(Action<Utf8JsonWriter> writeProperties)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            writeProperties(json);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void WriteTags(Utf8JsonWriter json, string name, IEnumerable<KeyValuePair<string, object?>> tags)
    {
        json.WriteStartObject(name);
        foreach (var (key, value) in tags)
            WriteTag(json, key, value);
        json.WriteEndObject();
    }

    public static void WriteTag(Utf8JsonWriter json, string key, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull(key);
                break;
            case bool flag:
                json.WriteBoolean(key, flag);
                break;
            case int or long or short or byte:
                json.WriteNumber(key, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case double or float:
                json.WriteNumber(key, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            default:
                json.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
