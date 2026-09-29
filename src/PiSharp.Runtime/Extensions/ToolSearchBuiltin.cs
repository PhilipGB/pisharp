using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Extensions;

/// <summary>Built-in discovery over the session's existing registry and loadout.</summary>
public static class ToolSearchBuiltin
{
    private static readonly JsonElement s_outputSchema = CreateOutputSchema();
    public static BuiltinExtensionDefinition Definition { get; } = new("tool-search", Configure,
        ShouldAutoEnable: registration => registration.ToolDefinitions.Any(tool =>
            tool.Exposure is ToolExposure.CodeMode or ToolExposure.Deferred));

    public static void Configure(ExtensionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var function = AIFunctionFactory.Create(Search, name: "tool_search",
            description: "Search deferred tool metadata and load matching tools for the next model request.");
        registration.AddTool(new PiSharpToolRegistration(function, ToolExposure.ModelOnly,
            PrepareLoadout: PrepareLoadout,
            AllowNestedInvocation: false, OutputSchema: s_outputSchema));
    }

    private static JsonElement CreateOutputSchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object","required":["loaded"],"properties":{"loaded":{"type":"array","items":{"type":"string"}}}}""");
        return document.RootElement.Clone();
    }

    private static ToolLoadoutChanges PrepareLoadout(ToolLoadoutSnapshot snapshot)
    {
        var sources = snapshot.Registered.Where(tool => tool.Exposure is ToolExposure.CodeMode or ToolExposure.Deferred)
            .Select(tool => tool.Namespace).OfType<PiSharpToolNamespace>()
            .DistinctBy(source => source.Name).ToArray();
        var listed = sources.Length == 0 ? "None currently enabled." : string.Join('\n', sources.Select(source =>
            "- " + source.Name + (string.IsNullOrWhiteSpace(source.Description) ? "" : ": " + FirstLine(source.Description))));
        return new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool_search"] = "Search deferred tool metadata and load matching tools for the next model request.\n" +
                "Available sources:\n" + listed
        });
    }

    private static PiSharpToolResult Search(
        [Description("Search query for deferred tools.")] string query,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken,
        [Description("Maximum tools to load, from 1 to 16. Defaults to 8.")] int? limit = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 512)
            throw new ArgumentException("query must contain 1 to 512 characters", nameof(query));
        var maximum = limit ?? 8;
        if (maximum is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(limit), "limit must be between 1 and 16");
        var context = PiSharpToolExecutionContext.Get(arguments);
        if (context is null) throw new InvalidOperationException("Tool search requires an active tool session.");
        var snapshot = context.Snapshot;
        var declared = snapshot.Declared.Select(tool => tool.Registration.Function.Name).ToHashSet(StringComparer.Ordinal);
        var callable = snapshot.Callable.Select(tool => tool.Function.Name).ToHashSet(StringComparer.Ordinal);
        var candidates = snapshot.Registered.Where(tool =>
            tool.Exposure is ToolExposure.CodeMode or ToolExposure.Deferred &&
            !declared.Contains(tool.Function.Name) && callable.Contains(tool.Function.Name)).Take(2000).ToArray();
        var matches = ToolSearchRanker.Rank(query, candidates, maximum, cancellationToken);
        if (matches.Length > 0) context.ActivateTools(matches.Select(tool => tool.Function.Name));
        var text = matches.Length == 0 ? "No matching tools found." :
            $"Loaded {matches.Length} tool{(matches.Length == 1 ? "" : "s")}. They are available from your next call:\n" +
            string.Join('\n', matches.Select(tool => "- " + tool.Function.Name + ": " + FirstLine(tool.Function.Description)));
        var loaded = matches.Select(tool => tool.Function.Name).ToArray();
        return new PiSharpToolResult(text, new { loaded }, JsonSerializer.SerializeToElement(new { loaded }));
    }

    private static string FirstLine(string? value)
    {
        var line = (value ?? "").Split(['\r', '\n'], 2)[0].Trim();
        return line.Length > 160 ? line[..160] : line;
    }
}

internal static class ToolSearchRanker
{
    private static readonly HashSet<string> s_stopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on",
        "or", "that", "the", "this", "to", "with"
    };
    private static readonly string[] s_pluralEndings = ["ches", "shes", "sses", "xes", "zes"];

    public static PiSharpToolRegistration[] Rank(string query, IReadOnlyList<PiSharpToolRegistration> tools,
        int limit, CancellationToken cancellationToken = default)
    {
        var terms = Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();
        if (terms.Length == 0 || tools.Count == 0) return [];
        var documents = tools.Select(tool =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CountTerms(Document(tool));
        }).ToArray();
        var lengths = documents.Select(document => document.Values.Sum()).ToArray();
        var averageLength = lengths.Average();
        var documentFrequency = terms.ToDictionary(term => term,
            term => documents.Count(document => document.ContainsKey(term)), StringComparer.Ordinal);
        var inverseFrequency = terms.ToDictionary(term => term,
            term => Math.Log(1 + (tools.Count - documentFrequency[term] + 0.5) /
                (documentFrequency[term] + 0.5)), StringComparer.Ordinal);
        var scored = new List<(PiSharpToolRegistration Tool, double Score, int Index)>();
        for (var index = 0; index < tools.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var score = 0d;
            foreach (var term in terms)
            {
                if (!documents[index].TryGetValue(term, out var frequency)) continue;
                var norm = 1.2 * (0.25 + 0.75 * lengths[index] / Math.Max(1, averageLength));
                score += inverseFrequency[term] * frequency * 2.2 / (frequency + norm);
            }
            if (score > 0) scored.Add((tools[index], score, index));
        }
        return scored.OrderByDescending(item => item.Score).ThenBy(item => item.Index)
            .Take(limit).Select(item => item.Tool).ToArray();
    }

    private static string Document(PiSharpToolRegistration tool)
    {
        var parts = new List<string> { tool.Function.Name, tool.Function.Name.Replace('_', ' '),
            tool.Function.Description ?? "", tool.Namespace?.Name ?? "", tool.Namespace?.Description ?? "" };
        AddSchema(tool.Function.JsonSchema, parts, 0);
        var document = string.Join(' ', parts);
        return document.Length > 4096 ? document[..4096] : document;
    }

    private static void AddSchema(JsonElement schema, List<string> parts, int depth)
    {
        if (depth >= 6 || parts.Count >= 128 || schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
            parts.Add(description.GetString() ?? "");
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            foreach (var property in properties.EnumerateObject())
            {
                if (parts.Count >= 128) break;
                parts.Add(property.Name);
                AddSchema(property.Value, parts, depth + 1);
            }
        if (schema.TryGetProperty("items", out var items)) AddSchema(items, parts, depth + 1);
        foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
            if (schema.TryGetProperty(key, out var variants) && variants.ValueKind == JsonValueKind.Array)
                foreach (var variant in variants.EnumerateArray())
                {
                    if (parts.Count >= 128) break;
                    AddSchema(variant, parts, depth + 1);
                }
    }

    private static Dictionary<string, int> CountTerms(string text)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in Tokenize(text)) result[term] = result.GetValueOrDefault(term) + 1;
        return result;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var word = new StringBuilder();
        for (var index = 0; index <= text.Length; index++)
        {
            var current = index < text.Length ? text[index] : ' ';
            var prior = index > 0 ? text[index - 1] : ' ';
            var next = index + 1 < text.Length ? text[index + 1] : ' ';
            var boundary = !char.IsAsciiLetterOrDigit(current) ||
                char.IsUpper(current) && (char.IsLower(prior) || char.IsDigit(prior) ||
                    char.IsUpper(prior) && char.IsLower(next));
            if (boundary && word.Length > 0)
            {
                var term = Stem(word.ToString().ToLowerInvariant());
                if (!s_stopWords.Contains(term)) yield return term;
                word.Clear();
            }
            if (char.IsAsciiLetterOrDigit(current)) word.Append(current);
        }
    }

    private static string Stem(string term)
    {
        if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal)) return term[..^3] + "y";
        if (term.Length > 4 && s_pluralEndings.Any(ending => term.EndsWith(ending, StringComparison.Ordinal)))
            return term[..^2];
        if (term.Length > 3 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal)) return term[..^1];
        return term;
    }
}
