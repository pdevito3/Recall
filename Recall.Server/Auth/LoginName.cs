namespace Recall.Server.Auth;

using System.Diagnostics.CodeAnalysis;
using System.Text;

/// <summary>
/// The name that a person types to sign in with a password (PWD-18). It holds the folded form: NFKC, then lower case,
/// then NFKC again, because a lower case letter can have a compatibility form. A login form folds the typed name the same
/// way, so <c>Ada</c> and <c>ada</c> name one credential.
/// </summary>
public sealed record LoginName
{
    public const int MaxLength = 64;

    /// <summary>The column holds code units, and a name of 64 code points outside the BMP has 128.</summary>
    public const int MaxStoredLength = MaxLength * 2;

    public const string NotValidMessage = "A login name has up to 64 letters, digits, dots, dashes, underscores, or @ signs.";
    public const string InUseMessage = "This login name is in use. Pick another.";

    private LoginName(string value) => Value = value;

    public string Value { get; }

    public static bool TryOf(string? typed, [NotNullWhen(true)] out LoginName? name)
    {
        name = null;
        if (Fold(typed) is not { } folded)
            return false;

        var count = 0;
        foreach (var rune in folded.EnumerateRunes())
        {
            if (!(Rune.IsLetter(rune) || Rune.IsDigit(rune) || rune.Value is '.' or '-' or '_' or '@'))
                return false;
            count++;
        }

        if (count is < 1 or > MaxLength)
            return false;
        name = new LoginName(folded);
        return true;
    }

    /// <summary>The stored value of a row. Only the column reads it back, and the column holds a name that <see cref="TryOf"/> made.</summary>
    public static LoginName FromStored(string value) => new(value);

    /// <summary>The key of a typed name for the wait of PWD-12, also for a name that is not valid, so each typed form of one name shares one wait.</summary>
    public static string KeyOf(string? typed) => Fold(typed) ?? typed ?? "";

    public override string ToString() => "[login name]";

    private static string? Fold(string? typed)
    {
        if (string.IsNullOrEmpty(typed))
            return null;
        try
        {
            return typed.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // A lone surrogate cannot normalize, and it is no letter.
            return null;
        }
    }
}
