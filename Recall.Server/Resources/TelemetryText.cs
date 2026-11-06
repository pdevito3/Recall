namespace Recall.Server.Resources;

using System.Globalization;

/// <summary>
/// The one limit on the length of a string that telemetry keeps. A caller controls many of these strings, such as an MCP
/// method or the message of an exception that names an argument. So without a limit, one request can fill the disk or
/// the heap of the export thread.
/// </summary>
public static class TelemetryText
{
    /// <summary>
    /// In UTF-16 code units, with the marker. The longest real values are stack traces: 8,081 for a failed start in
    /// <c>@x</c> and 5,558 for a failed save in <c>exception.stacktrace</c>. A request line, which Kestrel limits to 8 KiB, also fits.
    /// </summary>
    public const int MaxLength = 16_384;

    public const string CutMarker = "...[cut by Recall, length ";

    public static string Cap(string text)
    {
        if (text.Length <= MaxLength)
            return text;

        var marker = string.Create(CultureInfo.InvariantCulture, $"{CutMarker}{text.Length}]");
        var kept = MaxLength - marker.Length;
        // A cut between the two halves of a surrogate pair leaves text that JSON cannot encode
        if (char.IsHighSurrogate(text[kept - 1]))
            kept--;

        return string.Concat(text.AsSpan(0, kept), marker);
    }

    /// <summary>A value that is not a string stays as it is, so that no <c>ToString</c> runs on the thread of a request.</summary>
    public static object? Cap(object? value) => value is string text ? Cap(text) : value;
}
