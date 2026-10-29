using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Matches Pi's context estimate for the current prompt and agent history.</summary>
internal static class PiContextUsageEstimator
{
    private const int EstimatedImageCharacters = 4_800;
    private static readonly HashSet<string> s_constrainedSamplingTools = new(
        ["read", "bash", "edit", "write"], StringComparer.Ordinal);
    private static readonly JsonSerializerOptions s_jsonOptions = new(AIJsonUtilities.DefaultOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public static int Estimate(int systemPromptCharacters, IEnumerable<AITool> tools, IEnumerable<ChatMessage> messages)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(systemPromptCharacters);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(messages);

        var declarations = tools.ToArray();
        var toolCharacters = declarations.Length == 0 ? 0 : SerializeToolDeclarations(declarations).Length;
        var messageCharacters = messages.Select(MessageCharacters).ToArray();
        long tokens = EstimateTokens(systemPromptCharacters + toolCharacters);
        foreach (var characters in messageCharacters) tokens += EstimateTokens(characters);
        return (int)Math.Min(int.MaxValue, tokens);
    }

    internal static string SerializeToolDeclarations(IEnumerable<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        return JsonSerializer.Serialize(ToolDeclarations(tools), s_jsonOptions);
    }

    public static int EstimateAfterUsage(long usageTokens, IEnumerable<ChatMessage> messages)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(usageTokens);
        ArgumentNullException.ThrowIfNull(messages);

        long tokens = usageTokens;
        foreach (var message in messages) tokens += EstimateTokens(MessageCharacters(message));
        return (int)Math.Min(int.MaxValue, tokens);
    }

    internal static int EstimateMessage(ChatMessage message) =>
        (int)Math.Min(int.MaxValue, EstimateTokens(MessageCharacters(message)));

    internal static int EstimateCharacters(long characters) =>
        (int)Math.Min(int.MaxValue, EstimateTokens(characters));

    private static List<Dictionary<string, object?>> ToolDeclarations(IEnumerable<AITool> tools)
    {
        var declarations = new List<Dictionary<string, object?>>();
        foreach (var function in tools.OfType<AIFunctionDeclaration>())
        {
            var declaration = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = function.Name
            };
            if (function.Description is not null) declaration["description"] = function.Description;
            if (function.JsonSchema is { } schema) declaration["parameters"] = schema;
            if (s_constrainedSamplingTools.Contains(function.Name))
                declaration["constrainedSampling"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "json_schema",
                    ["strict"] = "prefer"
                };
            declarations.Add(declaration);
        }
        return declarations;
    }

    private static long MessageCharacters(ChatMessage message)
    {
        long characters = 0;
        foreach (var content in message.Contents)
        {
            characters += content switch
            {
                TextContent text => text.Text.Length,
                TextReasoningContent reasoning => reasoning.Text.Length,
                DataContent => EstimatedImageCharacters,
                FunctionCallContent call => call.Name.Length +
                    JsonSerializer.Serialize(call.Arguments, s_jsonOptions).Length,
                FunctionResultContent result => ResultCharacters(result.Result),
                _ => 0
            };
        }
        return characters;
    }

    private static long ResultCharacters(object? result) => result switch
    {
        null => 0,
        string text => text.Length,
        IEnumerable<AIContent> content => content.Sum(item => item switch
        {
            TextContent text => (long)text.Text.Length,
            DataContent => EstimatedImageCharacters,
            _ => 0
        }),
        _ => JsonSerializer.Serialize(result, s_jsonOptions).Length
    };

    private static long EstimateTokens(long characters) => (characters + 3) / 4;
}
