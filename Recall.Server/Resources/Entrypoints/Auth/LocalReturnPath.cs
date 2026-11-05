namespace Recall.Server.Resources.Entrypoints.Auth;

using System.Buffers;
using System.Globalization;
using System.Text;

public static class LocalReturnPath
{
    public const int MaxLength = 2048;

    private const string Root = "/";

    public static string Of(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxLength || raw[0] != '/')
            return Root;

        if (raw.Length > 1 && raw[1] == '/')
            return Root;

        foreach (var c in raw)
        {
            if (c == '\\' || char.IsControl(c))
                return Root;
        }

        return ToAscii(raw) is { Length: <= MaxLength } ascii ? ascii : Root;
    }

    // A header value can hold only ASCII, so each other character becomes its percent-encoded UTF-8 bytes.
    private static string? ToAscii(string path)
    {
        if (Ascii.IsValid(path))
            return path;

        var ascii = new StringBuilder(path.Length * 3);
        Span<byte> utf8 = stackalloc byte[4];
        for (var rest = path.AsSpan(); !rest.IsEmpty;)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var consumed) != OperationStatus.Done)
                return null;

            if (rune.IsAscii)
            {
                ascii.Append((char)rune.Value);
            }
            else
            {
                foreach (var b in utf8[..rune.EncodeToUtf8(utf8)])
                    ascii.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
            }

            rest = rest[consumed..];
        }

        return ascii.ToString();
    }
}
