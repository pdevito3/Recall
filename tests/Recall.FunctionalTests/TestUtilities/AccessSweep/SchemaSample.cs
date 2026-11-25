namespace Recall.FunctionalTests.TestUtilities.AccessSweep;

using System.Text.Json.Nodes;

// The value must only bind and pass the request rules, so that the access check of the handler gives the answer.
// It does not need to make sense to the domain.
public static class SchemaSample
{
    private static readonly Dictionary<string, string> StringsThatARuleChecksAgainstAList = new() { ["timeZone"] = "UTC" };

    public const string ImportEntryId = "sweep-import-entry";

    private static readonly Dictionary<string, string> StringsThatNameARecord = new() { ["clientSessionId"] = ImportEntryId };

    // A rule needs one of two optional values, so the sample sends this one. The dates of the sample are in the past.
    private static readonly Dictionary<string, bool> OptionalFlagsThatARuleNeeds = new() { ["neverExpires"] = true };

    // The first temporary password of a user needs a login name, and a later one ignores it.
    private static readonly Dictionary<string, string> OptionalStringsThatARuleNeeds = new() { ["loginName"] = "sweep" };

    public static JsonNode? Of(JsonNode schema, JsonNode root)
    {
        if (schema["$ref"]?.GetValue<string>() is { } reference)
            return Of(Resolve(root, reference), root);
        if (schema["enum"] is JsonArray { Count: > 0 } names)
            return names[0]?.DeepClone();
        if ((schema["oneOf"] ?? schema["anyOf"]) is JsonArray options)
            return Of(options.FirstOrDefault(option => !IsNullType(option!))
                ?? throw new SweepSetupException($"The sweep cannot make a value for the schema {schema.ToJsonString()}, because each option is null."), root);
        if (schema["allOf"] is not null)
            throw new SweepSetupException($"The sweep cannot make a value for the schema {schema.ToJsonString()}.");

        return TypeOf(schema) switch
        {
            "object" => ObjectOf(schema, root),
            "array" => ArrayOf(schema, root),
            "string" => StringOf(schema),
            "integer" => JsonValue.Create(schema["minimum"]?.GetValue<long>() ?? 0L),
            "number" => JsonValue.Create(schema["minimum"]?.GetValue<double>() ?? 0d),
            "boolean" => JsonValue.Create(false),
            _ => throw new SweepSetupException($"The sweep cannot make a value for the schema {schema.ToJsonString()}."),
        };
    }

    public static JsonNode? PropertyOf(string name, JsonNode property, JsonNode root)
        => StringsThatARuleChecksAgainstAList.TryGetValue(name, out var value)
            || StringsThatNameARecord.TryGetValue(name, out value)
            ? JsonValue.Create(value)
            : Of(property, root);

    public static IReadOnlyList<string> RequiredOf(JsonNode schema, JsonNode root)
    {
        var resolved = schema["$ref"]?.GetValue<string>() is { } reference ? Resolve(root, reference) : schema;
        return resolved["required"]?.AsArray().Select(name => name!.GetValue<string>()).ToList() ?? [];
    }

    private static JsonObject ObjectOf(JsonNode schema, JsonNode root)
    {
        var properties = schema["properties"]?.AsObject();
        var sample = new JsonObject();
        foreach (var name in RequiredOf(schema, root))
        {
            var property = properties?[name]
                ?? throw new SweepSetupException($"The schema requires {name}, but it has no such property.");
            sample[name] = PropertyOf(name, property, root);
        }
        foreach (var (name, flag) in OptionalFlagsThatARuleNeeds)
        {
            if (properties?[name] is not null && !sample.ContainsKey(name))
                sample[name] = JsonValue.Create(flag);
        }
        foreach (var (name, text) in OptionalStringsThatARuleNeeds)
        {
            if (properties?[name] is not null && !sample.ContainsKey(name))
                sample[name] = JsonValue.Create(text);
        }
        return sample;
    }

    private static JsonArray ArrayOf(JsonNode schema, JsonNode root)
    {
        var count = schema["minItems"]?.GetValue<int>() ?? 0;
        var items = schema["items"];
        if (count > 0 && items is null)
            throw new SweepSetupException($"The array schema {schema.ToJsonString()} needs items but names no item schema.");
        return [.. Enumerable.Range(0, count).Select(_ => Of(items!, root))];
    }

    private static JsonValue StringOf(JsonNode schema) => schema["format"]?.GetValue<string>() switch
    {
        _ when schema["pattern"] is not null
            => throw new SweepSetupException($"The sweep cannot make a value for the pattern of {schema.ToJsonString()}."),
        "date-time" => JsonValue.Create("2026-01-01T00:00:00Z"),
        "date" => JsonValue.Create("2026-01-01"),
        "uuid" => JsonValue.Create(Guid.Empty.ToString()),
        "email" => JsonValue.Create("sweep@example.com"),
        _ => JsonValue.Create(new string('a', Math.Max(1, schema["minLength"]?.GetValue<int>() ?? 0))),
    };

    private static string? TypeOf(JsonNode schema) => schema["type"] switch
    {
        JsonArray names => names.Select(name => name!.GetValue<string>()).FirstOrDefault(name => name != "null"),
        JsonValue name => name.GetValue<string>(),
        _ => schema["properties"] is not null ? "object" : null,
    };

    private static bool IsNullType(JsonNode schema) => schema["type"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
        && schema["type"]!.GetValue<string>() == "null";

    private static JsonNode Resolve(JsonNode root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
            throw new SweepSetupException($"The sweep resolves only a local $ref, not {reference}.");
        return reference[2..].Split('/').Aggregate(root, (node, segment) => node[segment]
            ?? throw new SweepSetupException($"The $ref {reference} does not resolve."));
    }
}
