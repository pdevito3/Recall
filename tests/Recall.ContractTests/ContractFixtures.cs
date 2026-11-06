namespace Recall.ContractTests;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shouldly;

internal sealed partial class ContractFixtures(string directory)
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, string> _stableIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _stableSequences = new(StringComparer.Ordinal);

    public static bool Saving => Environment.GetEnvironmentVariable("RECALL_SAVE_FIXTURES") == "1";

    public async Task CheckAsync(string name, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var path = Path.Combine(directory, $"{name}.json");
        var fixture = Normalize(body);

        if (Saving)
        {
            await File.WriteAllTextAsync(path, fixture);
            return;
        }

        File.Exists(path).ShouldBeTrue($"Run with RECALL_SAVE_FIXTURES=1 to save {name}.json.");
        fixture.ShouldBe((await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n"), $"{name}.json is the contract of a client. Change it only on purpose.");
    }

    private string Normalize(string body)
    {
        var node = JsonNode.Parse(body)!;
        Walk(node);
        var text = node.ToJsonString(Pretty);
        text = EntityIds().Replace(text, match =>
        {
            if (!_stableIds.TryGetValue(match.Value, out var id))
            {
                id = $"{match.Groups["prefix"].Value}_{_stableIds.Count + 1:D32}";
                _stableIds[match.Value] = id;
            }

            return id;
        });
        return text.ReplaceLineEndings("\n") + "\n";
    }

    private void Walk(JsonNode node)
    {
        if (node is JsonArray list)
        {
            foreach (var item in list.OfType<JsonNode>())
                Walk(item);
            return;
        }

        if (node is not JsonObject element)
            return;

        foreach (var (key, value) in element.ToList())
        {
            if (value is null)
                continue;

            if (key == "traceId")
                element[key] = "00000000000000000000000000000000";
            else if (key == "cursor" && value is JsonValue sequence)
                element[key] = StableSequence(sequence.GetValue<string>());
            else
                Walk(value);
        }
    }

    private string StableSequence(string sequence)
    {
        if (sequence == "0")
            return sequence;

        if (!_stableSequences.TryGetValue(sequence, out var stable))
        {
            stable = (_stableSequences.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _stableSequences[sequence] = stable;
        }

        return stable;
    }

    [GeneratedRegex("(?<prefix>[a-z]{3})_[0-9a-f]{32}")]
    private static partial Regex EntityIds();
}
