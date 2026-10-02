using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Core;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

internal sealed record AnthropicSystemUpdate(int BeforeAssistant, JsonObject Message);

internal sealed class AnthropicTranscriptRequest
{
    internal readonly AsyncLocal<IReadOnlyList<AnthropicSystemUpdate>?> Updates = new();
}

internal sealed class AnthropicTranscriptChatClient(IChatClient inner, ModelDescriptor model,
    AnthropicTranscriptRequest request, bool isOAuth = false) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var projection = Project(messages, options);
        var previous = request.Updates.Value;
        request.Updates.Value = projection.Updates;
        try
        {
            var response = await base.GetResponseAsync(projection.Messages, projection.Options, cancellationToken);
            if (isOAuth)
                foreach (var message in response.Messages)
                    message.Contents = AnthropicToolNames.MapCalls(message.Contents, name => projection.ToolNames.GetValueOrDefault(name, name));
            return response;
        }
        finally { request.Updates.Value = previous; }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var projection = Project(messages, options);
        var previous = request.Updates.Value;
        request.Updates.Value = projection.Updates;
        try
        {
            await foreach (var update in base.GetStreamingResponseAsync(projection.Messages, projection.Options, cancellationToken))
            {
                if (isOAuth)
                    update.Contents = AnthropicToolNames.MapCalls(update.Contents, name => projection.ToolNames.GetValueOrDefault(name, name));
                yield return update;
            }
        }
        finally { request.Updates.Value = previous; }
    }

    private Projection Project(IEnumerable<ChatMessage> source, ChatOptions? options)
    {
        var messages = source.ToList();
        var projectedTranscript = options?.AdditionalProperties?.TryGetValue("pisharp.toolTranscript", out var transcript) == true &&
            transcript is JsonElement { ValueKind: JsonValueKind.Array };
        if (projectedTranscript)
        {
            var revisions = ((JsonElement)options!.AdditionalProperties!["pisharp.toolTranscript"]!).EnumerateArray().ToArray();
            var projected = new List<ChatMessage>();
            for (var index = 0; index <= messages.Count; index++)
            {
                foreach (var revision in revisions.Where(revision => Math.Min(revision.GetProperty("afterMessageCount").GetInt32(), messages.Count) == index))
                    projected.Add(revision.GetProperty("message").Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)!);
                if (index < messages.Count) projected.Add(messages[index]);
            }
            messages = projected;
        }
        var initial = messages.FirstOrDefault()?.Role == ChatRole.System ? messages[0] : null;
        if (!projectedTranscript && !string.IsNullOrEmpty(options?.Instructions))
        {
            initial = new ChatMessage(ChatRole.System, options.Instructions);
            var definitions = (options.Tools ?? []).OfType<AIFunctionDeclaration>().Select(tool => new
            {
                name = tool.Name,
                description = tool.Description,
                parameters = tool.JsonSchema
            });
            SystemMessageTranscript.ImportMetadata(initial, JsonSerializer.SerializeToElement(new { toolsAdded = definitions }));
            messages.Insert(0, initial);
        }
        var systemSupport = Compatibility("supportsMidConvoSystemMessages", false);
        var initialTools = initial is null ? [] : SystemMessageTranscript.AddedTools(initial);
        var native = systemSupport && Compatibility("supportsMidConvoToolChanges", false) && initialTools.Length > 0;
        var declaredTools = native ? initialTools : SystemMessageTranscript.CurrentTools(messages);
        if (declaredTools.Count == 0 && !messages.Any(message => message.Role == ChatRole.System))
            declaredTools = (options?.Tools ?? []).OfType<AIFunctionDeclaration>().Select(tool =>
                JsonSerializer.SerializeToElement(new { name = tool.Name, description = tool.Description, parameters = tool.JsonSchema })).ToArray();
        var tools = declaredTools.Select((tool, index) => (BetaToolUnion)JsonSerializer.Deserialize<BetaTool>(
            ToolDefinition(tool, cache: index == declaredTools.Count - 1 && Compatibility("supportsCacheControlOnTools", true)).ToJsonString())!).ToList();
        if (native) tools.Add(JsonSerializer.Deserialize<BetaTool>("""
            {"name":"__pi_deferred_placeholder__","description":"Reserved placeholder. Never available. Never call this.","input_schema":{"type":"object","properties":{},"required":[]},"defer_loading":true}
            """)!);
        var systemText = systemSupport ? initial is null ? "" : SystemMessageTranscript.Text(initial)
            : SystemMessageTranscript.CurrentText(messages);
        var updates = new List<AnthropicSystemUpdate>();
        var assistantCount = 0;
        foreach (var message in initial is null ? messages : messages.Skip(1))
        {
            if (message.Role == ChatRole.Assistant) assistantCount++;
            if (message.Role != ChatRole.System || !systemSupport) continue;
            var blocks = new JsonArray();
            var text = SystemMessageTranscript.Text(message, update: true);
            if (text.Length > 0) blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            if (native)
            {
                var added = SystemMessageTranscript.AddedTools(message);
                var redefined = added.Select(tool => tool.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
                foreach (var name in SystemMessageTranscript.RemovedTools(message).Where(name => !redefined.Contains(name)))
                    blocks.Add(new JsonObject { ["type"] = "tool_removal", ["tool"] = new JsonObject { ["type"] = "tool_reference", ["name"] = WireName(name) } });
                foreach (var tool in added)
                    blocks.Add(new JsonObject { ["type"] = "tool_addition", ["tool"] = new JsonObject { ["type"] = "tool_definition", ["definition"] = ToolDefinition(tool) } });
            }
            if (blocks.Count > 0) updates.Add(new(assistantCount, new JsonObject { ["role"] = "system", ["content"] = blocks }));
        }
        var requestOptions = options?.Clone() ?? new ChatOptions();
        var originalFactory = requestOptions.RawRepresentationFactory;
        requestOptions.RawRepresentationFactory = client =>
        {
            var parameters = originalFactory?.Invoke(client) as MessageCreateParams ?? new MessageCreateParams
            {
                Model = options?.ModelId ?? model.Id,
                MaxTokens = options?.MaxOutputTokens ?? model.MaxOutputTokens ?? 16384,
                Messages = []
            };
            parameters = parameters with { Tools = tools };
            if (systemText.Length > 0)
                parameters = parameters with { System = new List<BetaTextBlockParam> { new() { Text = systemText, CacheControl = new BetaCacheControlEphemeral() } } };
            if (isOAuth)
            {
                var identity = new BetaTextBlockParam { Text = "You are Claude Code, Anthropic's official CLI for Claude.", CacheControl = new BetaCacheControlEphemeral() };
                parameters = parameters with
                {
                    System = systemText.Length > 0
                        ? new List<BetaTextBlockParam> { identity, new() { Text = systemText, CacheControl = new BetaCacheControlEphemeral() } }
                        : new List<BetaTextBlockParam> { identity },
                    Betas = [.. parameters.Betas ?? [], "claude-code-20250219", "oauth-2025-04-20"]
                };
            }
            if (native) parameters = parameters with { Betas = (parameters.Betas ?? []).Append((ApiEnum<string, AnthropicBeta>)"inline-tools-2026-09-15").Distinct().ToArray() };
            return parameters;
        };
        requestOptions.Instructions = null;
        requestOptions.Tools = [];
        var conversation = messages.Where(message => message.Role != ChatRole.System).Select(message =>
        {
            if (!isOAuth || message.Role != ChatRole.Assistant) return message;
            var copy = message.Clone();
            copy.Contents = AnthropicToolNames.MapCalls(message.Contents, WireName);
            return copy;
        }).ToArray();
        var names = SystemMessageTranscript.CurrentTools(messages).ToDictionary(tool => WireName(tool.GetProperty("name").GetString()!),
            tool => tool.GetProperty("name").GetString()!, StringComparer.OrdinalIgnoreCase);
        return new(conversation, requestOptions, updates, names);
    }

    private string WireName(string name) => isOAuth ? AnthropicToolNames.WireName(name) : name;

    private bool Compatibility(string name, bool fallback) =>
        model.Compatibility is { ValueKind: JsonValueKind.Object } compatibility && compatibility.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.True : fallback;

    private JsonObject ToolDefinition(JsonElement tool, bool cache = false)
    {
        var parameters = tool.GetProperty("parameters");
        var definition = new JsonObject
        {
            ["name"] = WireName(tool.GetProperty("name").GetString()!),
            ["description"] = tool.GetProperty("description").GetString(),
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = parameters.TryGetProperty("properties", out var properties) ? JsonNode.Parse(properties.GetRawText()) : new JsonObject(),
                ["required"] = parameters.TryGetProperty("required", out var required) ? JsonNode.Parse(required.GetRawText()) : new JsonArray()
            }
        };
        if (Compatibility("supportsEagerToolInputStreaming", true)) definition["eager_input_streaming"] = true;
        if (cache) definition["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        return definition;
    }

    private sealed record Projection(IReadOnlyList<ChatMessage> Messages, ChatOptions Options,
        IReadOnlyList<AnthropicSystemUpdate> Updates, IReadOnlyDictionary<string, string> ToolNames);
}
