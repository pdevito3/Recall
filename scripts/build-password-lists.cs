#!/usr/bin/env dotnet

// Builds the two word lists of password login from pinned public sources, and checks each source by its SHA-256.
//
//   dotnet run scripts/build-password-lists.cs
//
// common-passwords.txt: the SecLists top 1,000,000 passwords (MIT). A chosen password has at least 15 code points after
// NFKC (PWD-8), so only an entry of 15 or more can ever match. The script keeps those entries, in NFKC and lower case,
// with no repeat, in ordinal order.
//
// eff-large-wordlist.txt: the EFF long word list (CC BY 3.0 US), the words only, for the temporary passwords of PWD-14.

using System.Security.Cryptography;
using System.Text;

const int MinimumLength = 15;
const string CommonSource =
    "https://raw.githubusercontent.com/danielmiessler/SecLists/64e7d0b910015fa4e8dffa9fed7aba7d73e4673d/Passwords/Common-Credentials/xato-net-10-million-passwords-1000000.txt";
const string CommonSha256 = "424a3e03a17df0a2bc2b3ca749d81b04e79d59cb7aeec8876a5a3f308d0caf51";
const string WordsSource = "https://www.eff.org/files/2016/07/18/eff_large_wordlist.txt";
const string WordsSha256 = "addd35536511597a02fa0a9ff1e5284677b8883b83e986e43f15a3db996b903e";
const int WordCount = 7776;

var root = RepositoryRoot();
var target = Path.Combine(root, "Recall.Server", "Auth", "Passwords");
Directory.CreateDirectory(target);
using var http = new HttpClient();

var common = (await DownloadAsync(CommonSource, CommonSha256))
    .Split('\n')
    .Select(line => line.TrimEnd('\r').Normalize(NormalizationForm.FormKC).ToLowerInvariant())
    .Where(entry => entry.EnumerateRunes().Count() >= MinimumLength)
    .Distinct(StringComparer.Ordinal)
    .Order(StringComparer.Ordinal)
    .ToList();
await File.WriteAllTextAsync(Path.Combine(target, "common-passwords.txt"), string.Join('\n', common) + "\n");
Console.WriteLine($"common-passwords.txt: {common.Count} entries");

var words = (await DownloadAsync(WordsSource, WordsSha256))
    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Select(line => line.TrimEnd('\r').Split('\t')[1])
    .ToList();
if (words.Count != WordCount || words.Distinct(StringComparer.Ordinal).Count() != WordCount)
    throw new InvalidOperationException($"The EFF list must have {WordCount} words with no repeat, and it has {words.Count}.");
await File.WriteAllTextAsync(Path.Combine(target, "eff-large-wordlist.txt"), string.Join('\n', words) + "\n");
Console.WriteLine($"eff-large-wordlist.txt: {words.Count} words");

async Task<string> DownloadAsync(string url, string sha256)
{
    var bytes = await http.GetByteArrayAsync(url);
    var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
    if (actual != sha256)
        throw new InvalidOperationException($"{url} has the SHA-256 {actual}, and the script pins {sha256}.");
    return Encoding.UTF8.GetString(bytes);
}

static string RepositoryRoot()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Recall.slnx")))
        directory = directory.Parent;
    return directory?.FullName ?? throw new InvalidOperationException("Run the script inside the repository.");
}
