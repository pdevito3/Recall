namespace Recall.Server.Auth;

using System.Text;

/// <summary>The rules of a password that a person chooses (PWD-8, PWD-9). Each check here is pure, and the caller hashes the accepted text.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 15;
    public const int MaxLength = 256;

    public const string TooShortMessage = "Use at least 15 characters. A phrase of four words is long enough and easy to remember.";
    public const string TooLongMessage = "Use no more than 256 characters.";
    public const string CommonMessage = "This password is on a list of common or leaked passwords. Pick another.";
    public const string PersonalMessage = "A password cannot be your login name, your name, or Recall. Pick another.";

    /// <summary>The form that the Server hashes and checks. Null for text that cannot normalize, such as a lone surrogate.</summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null)
            return null;
        try
        {
            return typed.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Gives the normalized password to hash, or the refusal. A blank name of <paramref name="personNames"/> is skipped.</summary>
    public static PasswordChoice Check(string? typed, LoginName loginName, IEnumerable<string?> personNames)
    {
        if (Normalize(typed) is not { } password)
            return new PasswordChoice.Refused(TooShortMessage);

        var length = password.EnumerateRunes().Count();
        if (length < MinLength)
            return new PasswordChoice.Refused(TooShortMessage);
        if (length > MaxLength)
            return new PasswordChoice.Refused(TooLongMessage);

        var folded = password.ToLowerInvariant();
        if (CommonPasswords.Contains(folded))
            return new PasswordChoice.Refused(CommonMessage);

        // The word Recall has fewer than 15 characters, so the length rule already refuses it.
        string?[] personal = [loginName.Value, .. personNames];
        if (personal.Any(name => !string.IsNullOrWhiteSpace(name) && string.Equals(Normalize(name.Trim())?.ToLowerInvariant(), folded, StringComparison.Ordinal)))
            return new PasswordChoice.Refused(PersonalMessage);

        return new PasswordChoice.Accepted(password);
    }
}

public abstract record PasswordChoice
{
    public sealed record Accepted(string Password) : PasswordChoice
    {
        public override string ToString() => "[accepted password]";
    }

    public sealed record Refused(string Message) : PasswordChoice;
}

/// <summary>The shipped list of common and leaked passwords. <c>scripts/build-password-lists.cs</c> builds it, so each entry has 15 or more code points, in NFKC and lower case.</summary>
public static class CommonPasswords
{
    private const string ResourceName = "Recall.Server.Auth.Passwords.common-passwords.txt";

    private static readonly Lazy<HashSet<string>> Entries = new(Load);

    public static bool Contains(string folded) => Entries.Value.Contains(folded);

    private static HashSet<string> Load()
    {
        using var stream = typeof(CommonPasswords).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The Server has no resource {ResourceName}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var entries = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
                entries.Add(line);
        }

        return entries;
    }
}
