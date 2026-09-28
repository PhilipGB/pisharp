using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.Documents;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Projects MAF history and options into the AWS Converse request model.</summary>
internal static class BedrockRequestMapper
{
    private const string RedactedReasoningKey = "pisharp.bedrock.redactedReasoning";
    private const string CacheWriteCountKey = "bedrock.cacheWriteInputTokenCount";
    private const string CacheWriteOneHourCountKey = "bedrock.cacheWriteOneHourInputTokenCount";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ConverseStreamRequest Build(ModelDescriptor model, IEnumerable<ChatMessage> messages,
        ChatOptions? options, bool enablePromptCaching, bool oneHourCache, bool forcePromptCaching, string? region)
    {
        var history = messages.ToArray();
        var systemText = new List<string>();
        if (!string.IsNullOrWhiteSpace(options?.Instructions)) systemText.Add(Sanitize(options.Instructions));
        systemText.AddRange(history.Where(message => message.Role == ChatRole.System)
            .Select(message => string.Concat(message.Contents.OfType<TextContent>().Select(item => item.Text)))
            .Where(text => !string.IsNullOrWhiteSpace(text)).Select(Sanitize));

        var requestMessages = ConvertHistory(history, model);
        var additionalFields = BuildAdditionalModelRequestFields(model, options, region);
        var inference = new InferenceConfiguration();
        var maxTokens = options?.MaxOutputTokens ?? (IsClaude(model) ? model.MaxOutputTokens : null);
        if (maxTokens is > 0) inference.MaxTokens = maxTokens;
        if (options?.Temperature is { } temperature) inference.Temperature = Convert.ToSingle(temperature);
        if (options?.TopP is { } topP) inference.TopP = Convert.ToSingle(topP);
        if (options?.StopSequences is { Count: > 0 } stopSequences) inference.StopSequences = [.. stopSequences];

        var request = new ConverseStreamRequest
        {
            ModelId = model.Id,
            Messages = requestMessages,
            InferenceConfig = inference
        };
        if (systemText.Count > 0)
        {
            request.System = [new SystemContentBlock { Text = string.Join("\n", systemText) }];
            if (enablePromptCaching && SupportsPromptCaching(model, forcePromptCaching))
                request.System.Add(new SystemContentBlock { CachePoint = CreateCachePoint(oneHourCache) });
        }
        if (requestMessages.Count > 0 && enablePromptCaching && SupportsPromptCaching(model, forcePromptCaching))
        {
            var final = requestMessages[^1];
            if (final.Role == ConversationRole.User)
                final.Content.Add(new ContentBlock { CachePoint = CreateCachePoint(oneHourCache) });
        }
        if (additionalFields is not null) request.AdditionalModelRequestFields = ToDocument(additionalFields);
        request.ToolConfig = ConvertToolConfig(options);
        return request;
    }

    public static IReadOnlyDictionary<string, long>? ReadCacheWriteCounts(TokenUsage? usage)
    {
        if (usage is null) return null;
        var total = usage.CacheWriteInputTokens ?? 0;
        var oneHour = usage.CacheDetails?.Where(detail => detail.Ttl == CacheTTL.ONE_HOUR)
            .Sum(detail => detail.InputTokens ?? 0) ?? 0;
        if (total == 0 && oneHour == 0) return null;
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (total > 0) counts[CacheWriteCountKey] = total;
        if (oneHour > 0) counts[CacheWriteOneHourCountKey] = oneHour;
        return counts;
    }

    internal static bool IsRedactedReasoning(TextReasoningContent reasoning) =>
        reasoning.AdditionalProperties?.TryGetValue(RedactedReasoningKey, out var value) == true &&
        value is true or JsonElement { ValueKind: JsonValueKind.True };

    internal static void MarkRedactedReasoning(TextReasoningContent reasoning)
    {
        reasoning.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        reasoning.AdditionalProperties[RedactedReasoningKey] = true;
    }

    private static List<Message> ConvertHistory(IReadOnlyList<ChatMessage> history, ModelDescriptor model)
    {
        var result = new List<Message>();
        var pendingToolResults = new List<ContentBlock>();
        void FlushToolResults()
        {
            if (pendingToolResults.Count == 0) return;
            result.Add(new Message { Role = ConversationRole.User, Content = [.. pendingToolResults] });
            pendingToolResults.Clear();
        }

        foreach (var message in history)
        {
            if (message.Role == ChatRole.System) continue;
            var results = message.Contents.OfType<FunctionResultContent>().ToArray();
            if (message.Role == ChatRole.Tool || results.Length > 0)
            {
                if (results.Length == 0)
                    throw new InvalidDataException("Amazon Bedrock tool history requires a correlated function result.");
                foreach (var functionResult in results)
                    pendingToolResults.Add(ConvertToolResult(functionResult, message.Contents.OfType<DataContent>()));
                continue;
            }

            FlushToolResults();
            var role = message.Role == ChatRole.Assistant ? ConversationRole.Assistant : ConversationRole.User;
            var content = ConvertMessageContent(message, model);
            if (content.Count == 0) continue;
            result.Add(new Message { Role = role, Content = content });
        }
        FlushToolResults();
        return result;
    }

    private static List<ContentBlock> ConvertMessageContent(ChatMessage message, ModelDescriptor model)
    {
        var content = new List<ContentBlock>();
        var items = message.Contents;
        for (var index = 0; index < items.Count; index++)
        {
            switch (items[index])
            {
                case TextContent text:
                    if (!string.IsNullOrWhiteSpace(text.Text)) content.Add(new ContentBlock { Text = Sanitize(text.Text) });
                    break;
                case DataContent data when IsImage(data):
                    content.Add(new ContentBlock { Image = ConvertImage(data) });
                    break;
                case FunctionCallContent call:
                    content.Add(new ContentBlock
                    {
                        ToolUse = new ToolUseBlock
                        {
                            ToolUseId = NormalizeToolCallId(call.CallId),
                            Name = call.Name,
                            Input = ToDocument(call.Arguments ?? new Dictionary<string, object?>())
                        }
                    });
                    break;
                case TextReasoningContent reasoning when message.Role == ChatRole.Assistant:
                    var textBuilder = new System.Text.StringBuilder();
                    string? signature = null;
                    var redacted = false;
                    while (index < items.Count && items[index] is TextReasoningContent reasoningPart)
                    {
                        textBuilder.Append(reasoningPart.Text);
                        if (!string.IsNullOrWhiteSpace(reasoningPart.ProtectedData))
                            signature = reasoningPart.ProtectedData;
                        redacted |= IsRedactedReasoning(reasoningPart);
                        index++;
                    }
                    index--;
                    var thinkingText = Sanitize(textBuilder.ToString());
                    if (redacted)
                    {
                        if (TryDecodeBase64(signature, out var redactedPayload))
                            content.Add(new ContentBlock { ReasoningContent = new ReasoningContentBlock { RedactedContent = redactedPayload } });
                    }
                    else if (thinkingText.Trim().Length > 0)
                    {
                        if (IsClaude(model) && !string.IsNullOrWhiteSpace(signature))
                        {
                            content.Add(new ContentBlock
                            {
                                ReasoningContent = new ReasoningContentBlock
                                {
                                    ReasoningText = new ReasoningTextBlock { Text = thinkingText, Signature = signature }
                                }
                            });
                        }
                        else if (IsClaude(model))
                        {
                            // A partial or externally persisted Claude history block without its
                            // signature is rejected by Bedrock; match Pi by replaying it as text.
                            content.Add(new ContentBlock { Text = thinkingText });
                        }
                        else
                        {
                            content.Add(new ContentBlock
                            {
                                ReasoningContent = new ReasoningContentBlock
                                {
                                    ReasoningText = new ReasoningTextBlock { Text = thinkingText }
                                }
                            });
                        }
                    }
                    break;
            }
        }
        if (content.Count == 0 && message.Role == ChatRole.User)
            content.Add(new ContentBlock { Text = "<empty>" });
        return content;
    }

    private static ContentBlock ConvertToolResult(FunctionResultContent result, IEnumerable<DataContent> dataContent)
    {
        var blocks = new List<ToolResultContentBlock>();
        var text = result.Exception is null ? ResultText(result.Result) : result.Exception.Message;
        if (!string.IsNullOrWhiteSpace(text)) blocks.Add(new ToolResultContentBlock { Text = Sanitize(text) });
        foreach (var data in dataContent.Where(IsImage))
            blocks.Add(new ToolResultContentBlock { Image = ConvertImage(data) });
        if (blocks.Count == 0) blocks.Add(new ToolResultContentBlock { Text = "<empty>" });
        return new ContentBlock
        {
            ToolResult = new ToolResultBlock
            {
                ToolUseId = NormalizeToolCallId(result.CallId),
                Content = blocks,
                Status = result.Exception is null ? ToolResultStatus.Success : ToolResultStatus.Error
            }
        };
    }

    private static ToolConfiguration? ConvertToolConfig(ChatOptions? options)
    {
        var declarations = options?.Tools?.OfType<AIFunctionDeclaration>().ToArray() ?? [];
        if (declarations.Length == 0 || options?.ToolMode is NoneChatToolMode) return null;
        var tools = declarations.Select(declaration => new Amazon.BedrockRuntime.Model.Tool
        {
            ToolSpec = new ToolSpecification
            {
                Name = declaration.Name,
                Description = declaration.Description,
                InputSchema = new ToolInputSchema
                {
                    Json = declaration.JsonSchema is { } schema ? ToDocument(CleanSchema(schema)) :
                        Document.FromObject(new { type = "object", properties = new { } })
                }
            }
        }).ToList();

        var choice = options?.ToolMode switch
        {
            RequiredChatToolMode => new ToolChoice { Any = new AnyToolChoice() },
            _ => new ToolChoice { Auto = new AutoToolChoice() }
        };
        return new ToolConfiguration { Tools = tools, ToolChoice = choice };
    }

    private static Document? BuildAdditionalModelRequestFields(ModelDescriptor model, ChatOptions? options, string? region)
    {
        if (options?.Reasoning is null || model.Reasoning != true || !IsClaude(model)) return null;
        var level = options.Reasoning.Effort switch
        {
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            ReasoningEffort.ExtraHigh => SupportsNativeXhigh(model) ? "xhigh" : "high",
            _ => "high"
        };
        var mapped = ReadThinkingLevelMap(model, level) ?? level;
        var adaptive = SupportsAdaptiveThinking(model);
        var govCloud = region?.StartsWith("us-gov-", StringComparison.OrdinalIgnoreCase) == true ||
            (BedrockProviderOptions.GetModelArnRegion(model.Id)?.StartsWith("us-gov-", StringComparison.OrdinalIgnoreCase) ?? false);
        var thinking = adaptive
            ? new JsonObject { ["type"] = "adaptive" }
            : new JsonObject { ["type"] = "enabled", ["budget_tokens"] = ThinkingBudget(mapped) };
        if (!govCloud) thinking["display"] = "summarized";
        var fields = new JsonObject { ["thinking"] = thinking };
        if (adaptive)
            fields["output_config"] = new JsonObject { ["effort"] = NormalizeEffort(mapped) };
        else
            fields["anthropic_beta"] = new JsonArray("interleaved-thinking-2025-05-14");
        return ToDocument(fields);
    }

    private static bool SupportsPromptCaching(ModelDescriptor model, bool force)
    {
        if (force) return true;
        if (!IsClaude(model)) return false;
        var value = (model.Id + " " + model.Name).ToLowerInvariant().Replace('_', '-');
        return value.Contains("fable-5", StringComparison.Ordinal) ||
            value.Contains("opus-5", StringComparison.Ordinal) ||
            value.Contains("sonnet-5", StringComparison.Ordinal) ||
            value.Contains("claude-opus-4-", StringComparison.Ordinal) ||
            value.Contains("claude-sonnet-4-", StringComparison.Ordinal) ||
            value.Contains("claude-haiku-4-", StringComparison.Ordinal) ||
            value.Contains("claude-3-7-sonnet", StringComparison.Ordinal) ||
            value.Contains("claude-3-5-haiku", StringComparison.Ordinal);
    }

    private static bool IsClaude(ModelDescriptor model) =>
        (model.Id + " " + model.Name).Contains("claude", StringComparison.OrdinalIgnoreCase);

    private static bool SupportsAdaptiveThinking(ModelDescriptor model)
    {
        var value = (model.Id + " " + model.Name).ToLowerInvariant().Replace('_', '-');
        return value.Contains("opus-4-6", StringComparison.Ordinal) ||
            value.Contains("opus-4-7", StringComparison.Ordinal) ||
            value.Contains("opus-4-8", StringComparison.Ordinal) ||
            value.Contains("opus-5", StringComparison.Ordinal) ||
            value.Contains("sonnet-4-6", StringComparison.Ordinal) ||
            value.Contains("sonnet-5", StringComparison.Ordinal) ||
            value.Contains("fable-5", StringComparison.Ordinal);
    }

    private static bool SupportsNativeXhigh(ModelDescriptor model)
    {
        var value = (model.Id + " " + model.Name).ToLowerInvariant().Replace('_', '-');
        return value.Contains("opus-4-7", StringComparison.Ordinal) ||
            value.Contains("opus-4-8", StringComparison.Ordinal) ||
            value.Contains("opus-5", StringComparison.Ordinal) ||
            value.Contains("sonnet-5", StringComparison.Ordinal) ||
            value.Contains("fable-5", StringComparison.Ordinal);
    }

    private static string? ReadThinkingLevelMap(ModelDescriptor model, string level)
    {
        if (model.ThinkingLevelMap is not { ValueKind: JsonValueKind.Object } map ||
            !map.TryGetProperty(level, out var value) || value.ValueKind != JsonValueKind.String) return null;
        return value.GetString()?.ToLowerInvariant();
    }

    private static string NormalizeEffort(string level) => level switch
    {
        "minimal" or "low" => "low",
        "medium" => "medium",
        "xhigh" => "xhigh",
        _ => "high"
    };

    private static int ThinkingBudget(string level) => level switch
    {
        "minimal" => 1024,
        "low" => 2048,
        "medium" => 8192,
        _ => 16384
    };

    private static CachePointBlock CreateCachePoint(bool oneHour) => new()
    {
        Type = CachePointType.Default,
        Ttl = oneHour ? CacheTTL.ONE_HOUR : null
    };

    private static ImageBlock ConvertImage(DataContent data)
    {
        var format = data.MediaType.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ImageFormat.Jpeg,
            "image/png" => ImageFormat.Png,
            "image/gif" => ImageFormat.Gif,
            "image/webp" => ImageFormat.Webp,
            _ => throw new NotSupportedException($"Amazon Bedrock does not support image media type '{data.MediaType}'.")
        };
        return new ImageBlock { Format = format, Source = new ImageSource { Bytes = new MemoryStream(data.Data.ToArray()) } };
    }

    private static bool IsImage(DataContent content) => content.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeToolCallId(string value)
    {
        var normalized = new string(value.Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'
            ? character : '_').Take(64).ToArray());
        return normalized.Length == 0 ? "call" : normalized;
    }

    private static bool TryDecodeBase64(string? value, out MemoryStream? stream)
    {
        stream = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { stream = new MemoryStream(Convert.FromBase64String(value), writable: false); return true; }
        catch (FormatException) { return false; }
    }

    private static Document ToDocument(object value) => Document.FromObject(value);

    private static JsonElement CleanSchema(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        StripSchemaMetadata(node);
        return JsonSerializer.SerializeToElement(node, JsonOptions);
    }

    private static void StripSchemaMetadata(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(item => item.Key).Where(key => key is "$schema" or "$id" or "$anchor" or
                         "$dynamicAnchor" or "$vocabulary" or "$comment" or "$defs" or "definitions").ToArray())
                obj.Remove(key);
            foreach (var child in obj.Select(item => item.Value)) StripSchemaMetadata(child);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array) StripSchemaMetadata(child);
        }
    }

    private static string ResultText(object? value) => value switch
    {
        null => "",
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(value, JsonOptions)
    };

    private static string Sanitize(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (!char.IsSurrogate(character)) { builder.Append(character); continue; }
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                builder.Append(character).Append(text[++index]);
                continue;
            }
            builder.Append('\uFFFD');
        }
        return builder.ToString();
    }
}
