namespace Recall.Server.Auth;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The password that an Admin issues (PWD-14): 4 random words of the EFF long list, joined by <c>-</c>. The list has
/// 7,776 words, so a password has about 51 bits, and the person replaces it at the first sign-in.
/// </summary>
public static class TemporaryPassword
{
    public const int WordCount = 4;

    private const string ResourceName = "Recall.Server.Auth.Passwords.eff-large-wordlist.txt";

    private static readonly Lazy<string[]> List = new(Load);

    public static IReadOnlyList<string> Words => List.Value;

    public static string New() => From(count => RandomNumberGenerator.GetInt32(count));

    /// <summary>The password of the words that <paramref name="pick"/> chooses. It takes the size of the list and gives an index below it.</summary>
    internal static string From(Func<int, int> pick)
        => string.Join('-', Enumerable.Range(0, WordCount).Select(_ => List.Value[pick(List.Value.Length)]));

    private static string[] Load()
    {
        using var stream = typeof(TemporaryPassword).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The Server has no resource {ResourceName}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var words = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
                words.Add(line);
        }

        return [.. words];
    }
}
