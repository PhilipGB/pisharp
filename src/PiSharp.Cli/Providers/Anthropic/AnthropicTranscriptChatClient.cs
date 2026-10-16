using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Exceptions;
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
    internal readonly AsyncLocal<AnthropicRequestAttempt?> Attempt = new();
}

internal sealed class AnthropicRequestAttempt
{
    internal System.Net.HttpStatusCode? StatusCode { get; set; }
}

internal sealed class AnthropicTranscriptChatClient(IChatClient inner, ModelDescriptor model,
    AnthropicTranscriptRequest request, bool isOAuth = false) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var source = messages.ToArray();
        var projection = Project(source, options);
        var previous = request.Updates.Value;
        var previousAttempt = request.Attempt.Value;
        request.Updates.Value = projection.Updates;
        request.Attempt.Value = new();
        try
        {
            ChatResponse response;
            try { response = await base.GetResponseAsync(projection.Messages, projection.Options, cancellationToken); }
            catch (AnthropicApiException error) when (CanFallback(error, projection, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                projection = Project(source, options, disablePreferredStrict: true);
                request.Updates.Value = projection.Updates;
                request.Attempt.Value = new();
                var diagnostic = FallbackDiagnostic(error);
                try { response = await base.GetResponseAsync(projection.Messages, projection.Options, cancellationToken); }
                catch (Exception rejected) { rejected.Data["diagnostics"] = diagnostic; throw; }
                response.AdditionalProperties ??= new();
                response.AdditionalProperties["diagnostics"] = diagnostic;
                foreach (var message in response.Messages.Where(message => message.Role == ChatRole.Assistant))
                {
                    message.AdditionalProperties ??= new();
                    message.AdditionalProperties["diagnostics"] = diagnostic;
                }
            }
            if (isOAuth)
                foreach (var message in response.Messages)
                    message.Contents = AnthropicToolNames.MapCalls(message.Contents, name => projection.ToolNames.GetValueOrDefault(name, name));
            return response;
        }
        finally { request.Updates.Value = previous; request.Attempt.Value = previousAttempt; }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var source = messages.ToArray();
        var projection = Project(source, options);
        var previous = request.Updates.Value;
        var previousAttempt = request.Attempt.Value;
        request.Updates.Value = projection.Updates;
        try
        {
            JsonElement? diagnostic = null;
            for (var attempt = 0; ; attempt++)
            {
                request.Attempt.Value = new();
                await using var stream = base.GetStreamingResponseAsync(projection.Messages, projection.Options, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                var started = false;
                var annotated = false;
                var retry = false;
                while (true)
                {
                    var next = false;
                    Exception? failure = null;
                    try { next = await stream.MoveNextAsync(); }
                    catch (Exception error) { failure = error; }
                    if (failure is not null)
                    {
                        if (attempt == 0 && !started && failure is AnthropicApiException apiError &&
                            CanFallback(apiError, projection, cancellationToken))
                        {
                            diagnostic = FallbackDiagnostic(apiError);
                            retry = true;
                            break;
                        }
                        if (diagnostic is { } rejectedDiagnostic) failure.Data["diagnostics"] = rejectedDiagnostic;
                        ExceptionDispatchInfo.Capture(failure).Throw();
                    }
                    if (!next) break;
                    started = true;
                    var update = stream.Current;
                    if (!annotated && diagnostic is { } fallback)
                    {
                        update.AdditionalProperties ??= new();
                        update.AdditionalProperties["diagnostics"] = fallback;
                        annotated = true;
                    }
                    if (isOAuth)
                        update.Contents = AnthropicToolNames.MapCalls(update.Contents, name => projection.ToolNames.GetValueOrDefault(name, name));
                    yield return update;
                }
                if (!retry) break;
                cancellationToken.ThrowIfCancellationRequested();
                projection = Project(source, options, disablePreferredStrict: true);
                request.Updates.Value = projection.Updates;
            }
        }
        finally { request.Updates.Value = previous; request.Attempt.Value = previousAttempt; }
    }

    private Projection Project(IEnumerable<ChatMessage> source, ChatOptions? options, bool disablePreferredStrict = false)
    {
        var messages = AnthropicToolResultProjection.Project(source).ToList();
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
            var definitions = (options.Tools ?? []).OfType<AIFunctionDeclaration>().Select(ToolDeclarationJson.Create);
            SystemMessageTranscript.ImportMetadata(initial, JsonSerializer.SerializeToElement(new { toolsAdded = definitions }));
            messages.Insert(0, initial);
        }
        var systemSupport = Compatibility("supportsMidConvoSystemMessages", false);
        var initialTools = initial is null ? [] : SystemMessageTranscript.AddedTools(initial);
        var native = systemSupport && Compatibility("supportsMidConvoToolChanges", false) && initialTools.Length > 0;
        var declaredTools = native ? initialTools : SystemMessageTranscript.CurrentTools(messages);
        if (declaredTools.Count == 0 && !messages.Any(message => message.Role == ChatRole.System))
            declaredTools = (options?.Tools ?? []).OfType<AIFunctionDeclaration>().Select(ToolDeclarationJson.Create).ToArray();
        var toolDefinitions = new AnthropicToolDefinitionProjection(Compatibility("supportsStrictTools", false),
            Compatibility("supportsEagerToolInputStreaming", true), WireName, disablePreferredStrict);
        var tools = declaredTools.Select((tool, index) => (BetaToolUnion)JsonSerializer.Deserialize<BetaTool>(
            toolDefinitions.Create(tool, cache: index == declaredTools.Count - 1 && Compatibility("supportsCacheControlOnTools", true)).ToJsonString())!).ToList();
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
                    blocks.Add(new JsonObject { ["type"] = "tool_addition", ["tool"] = new JsonObject { ["type"] = "tool_definition", ["definition"] = toolDefinitions.Create(tool) } });
            }
            if (blocks.Count > 0) updates.Add(new(assistantCount, new JsonObject { ["role"] = "system", ["content"] = blocks }));
        }
        var thinkingBudgets = ThinkingBudgetPolicy.Read(options?.AdditionalProperties);
        var requestOptions = options?.Clone() ?? new ChatOptions();
        requestOptions.AdditionalProperties?.Remove(ThinkingBudgetPolicy.SettingsProperty);
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
            var adaptiveThinking = SupportsAdaptiveThinking() ||
                parameters.Thinking is { } configuredThinking && configuredThinking.TryPickAdaptive(out _);
            if (options?.Reasoning is { } reasoning && !adaptiveThinking)
            {
                var level = reasoning.Effort switch
                {
                    ReasoningEffort.Low => "low",
                    ReasoningEffort.Medium => "medium",
                    ReasoningEffort.High => "high",
                    ReasoningEffort.ExtraHigh => "xhigh",
                    _ => "high"
                };
                var adjusted = ThinkingBudgetPolicy.AdjustMaxTokens(options.MaxOutputTokens,
                    model.MaxOutputTokens ?? 16384, level, thinkingBudgets);
                var budget = Math.Min(adjusted.ThinkingBudget,
                    Math.Max(0, adjusted.MaxTokens - ThinkingBudgetPolicy.MinimumAnswerTokens));
                var thinking = JsonSerializer.Deserialize<BetaThinkingConfigParam>(
                    $"{{\"type\":\"enabled\",\"budget_tokens\":{budget}}}")!;
                parameters = parameters with { MaxTokens = adjusted.MaxTokens, Thinking = thinking };
            }
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
        return new(conversation, requestOptions, updates, names, toolDefinitions.HasPreferredStrictTools);
    }

    private string WireName(string name) => isOAuth ? AnthropicToolNames.WireName(name) : name;

    private bool Compatibility(string name, bool fallback) =>
        model.Compatibility is { ValueKind: JsonValueKind.Object } compatibility && compatibility.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.True : fallback;

    private bool SupportsAdaptiveThinking()
    {
        if (Compatibility("forceAdaptiveThinking", false)) return true;
        var value = (model.Id + " " + model.Name).ToLowerInvariant().Replace('_', '-');
        return value.Contains("opus-4-6", StringComparison.Ordinal) ||
            value.Contains("opus-4-7", StringComparison.Ordinal) ||
            value.Contains("opus-4-8", StringComparison.Ordinal) ||
            value.Contains("opus-5", StringComparison.Ordinal) ||
            value.Contains("sonnet-4-6", StringComparison.Ordinal) ||
            value.Contains("sonnet-5", StringComparison.Ordinal) ||
            value.Contains("fable-5", StringComparison.Ordinal);
    }

    private bool CanFallback(AnthropicApiException error, Projection projection, CancellationToken token)
    {
        if (token.IsCancellationRequested || !projection.HasPreferredStrictTools || error.StatusCode != System.Net.HttpStatusCode.BadRequest ||
            request.Attempt.Value?.StatusCode != error.StatusCode)
            return false;
        try
        {
            using var body = JsonDocument.Parse(error.ResponseBody);
            return body.RootElement.TryGetProperty("error", out var detail) && detail.ValueKind == JsonValueKind.Object &&
                detail.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "invalid_request_error" &&
                detail.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
                new[] { "The compiled grammar is too large", "Too many strict tools (", "Schemas contains too many parameters with union types (" }
                    .Any(prefix => message.GetString()!.Contains(prefix, StringComparison.Ordinal));
        }
        catch (JsonException) { return false; }
    }

    private static JsonElement FallbackDiagnostic(AnthropicApiException error) => JsonSerializer.SerializeToElement(new[]
    {
        new { type = "anthropic_strict_tools_fallback", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            error = new { message = error.Message }, details = new { reason = "strict_tool_limits_exceeded" } }
    });

    private sealed record Projection(IReadOnlyList<ChatMessage> Messages, ChatOptions Options,
        IReadOnlyList<AnthropicSystemUpdate> Updates, IReadOnlyDictionary<string, string> ToolNames, bool HasPreferredStrictTools);
}
