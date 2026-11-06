namespace Recall.Server.Databases;

using System.Buffers;
using System.Text;

public static class StorableText
{
    /// <summary>
    /// False when <paramref name="value"/> has a NUL, at which the SQLite text functions stop, or a lone surrogate, which
    /// UTF-8 cannot encode, so the stored text would not be the text of the caller.
    /// </summary>
    public static bool CanStore(string value)
    {
        var rest = value.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var consumed) != OperationStatus.Done || rune.Value == 0)
                return false;
            rest = rest[consumed..];
        }

        return true;
    }
}
