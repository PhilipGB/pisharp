using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcStateCommandHandler(
    JsonLineWriter output,
    Func<ConversationRun> currentRun,
    Func<string?> getThinkingLevel,
    Func<bool> isStreaming,
    Func<JsonElement?> getModel)
{
    public async Task<bool> TryHandleAsync(string command, JsonElement? id, CancellationToken cancellationToken)
    {
        if (command != "get_state") return false;

        var run = currentRun();
        var conversation = run.Conversation;
        var queue = run.GetPendingPrompts();
        await output.EmitAsync(new
        {
            id,
            type = "response",
            command,
            success = true,
            data = new RpcSessionState(
                getModel(),
                getThinkingLevel() ?? "off",
                isStreaming(),
                run.IsCompacting,
                run.SteeringMode.ToSettingValue(),
                run.FollowUpMode.ToSettingValue(),
                run.SessionFile,
                conversation.Id,
                conversation.Name,
                run.AutoCompactionEnabled,
                conversation.ActiveMessages().Count,
                queue.InDeliveryOrder.Count)
        }, cancellationToken);
        return true;
    }
}

internal sealed record RpcSessionState(
    [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Model,
    [property: JsonPropertyName("thinkingLevel")] string ThinkingLevel,
    [property: JsonPropertyName("isStreaming")] bool IsStreaming,
    [property: JsonPropertyName("isCompacting")] bool IsCompacting,
    [property: JsonPropertyName("steeringMode")] string SteeringMode,
    [property: JsonPropertyName("followUpMode")] string FollowUpMode,
    [property: JsonPropertyName("sessionFile"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionFile,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("sessionName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SessionName,
    [property: JsonPropertyName("autoCompactionEnabled")] bool AutoCompactionEnabled,
    [property: JsonPropertyName("messageCount")] int MessageCount,
    [property: JsonPropertyName("pendingMessageCount")] int PendingMessageCount);

internal sealed record RpcModelSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("api")] string Api,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
    [property: JsonPropertyName("cost"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RpcModelCostSnapshot? Cost,
    [property: JsonPropertyName("reasoning")] bool Reasoning,
    [property: JsonPropertyName("contextWindow"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ContextWindow,
    [property: JsonPropertyName("maxTokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxTokens,
    [property: JsonPropertyName("inputLimits"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RpcModelInputLimits? InputLimits)
{
    public static RpcModelSnapshot From(ModelSelection selection)
    {
        var model = selection.Model;
        return new RpcModelSnapshot(
            model.Id,
            model.Name ?? model.Id,
            model.Api ?? ProviderChatClientFactory.ResolveProtocol(selection),
            selection.Provider.Id,
            selection.Provider.Endpoint.ToString().TrimEnd('/'),
            model.Input ?? ["text"],
            model.Pricing is { } pricing ? RpcModelCostSnapshot.From(pricing) : null,
            model.Reasoning == true,
            model.ContextLength,
            model.MaxOutputTokens,
            RpcModelInputLimits.From(model.InputLimits));
    }
}

internal sealed record RpcModelCostSnapshot(
    [property: JsonPropertyName("input")] decimal Input,
    [property: JsonPropertyName("output")] decimal Output,
    [property: JsonPropertyName("cacheRead"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CacheRead,
    [property: JsonPropertyName("cacheWrite"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CacheWrite,
    [property: JsonPropertyName("tiers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RpcModelCostTier>? Tiers)
{
    public static RpcModelCostSnapshot From(ModelPricing pricing) => new(
        pricing.Input,
        pricing.Output,
        pricing.CachedInput,
        pricing.CachedWrite,
        pricing.Tiers?.Select(RpcModelCostTier.From).ToArray());
}

internal sealed record RpcModelCostTier(
    [property: JsonPropertyName("inputTokensAbove")] int InputTokensAbove,
    [property: JsonPropertyName("input")] decimal Input,
    [property: JsonPropertyName("output")] decimal Output,
    [property: JsonPropertyName("cacheRead"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CacheRead,
    [property: JsonPropertyName("cacheWrite"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CacheWrite)
{
    public static RpcModelCostTier From(ModelPricingTier tier) => new(
        tier.InputTokensAbove, tier.Input, tier.Output, tier.CachedInput, tier.CachedWrite);
}

internal sealed record RpcModelInputLimits(
    [property: JsonPropertyName("images"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RpcModelImageInputLimits? Images)
{
    public static RpcModelInputLimits? From(ModelInputLimits? limits) => limits?.Images is { } images
        ? new RpcModelInputLimits(RpcModelImageInputLimits.From(images))
        : null;
}

internal sealed record RpcModelImageInputLimits(
    [property: JsonPropertyName("resize"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RpcModelImageResizeOptions? Resize)
{
    public static RpcModelImageInputLimits From(ModelImageInputLimits limits) =>
        new(limits.Resize is { } resize ? RpcModelImageResizeOptions.From(resize) : null);
}

internal sealed record RpcModelImageResizeOptions(
    [property: JsonPropertyName("maxWidth"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxWidth,
    [property: JsonPropertyName("maxHeight"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxHeight,
    [property: JsonPropertyName("maxBytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxBytes,
    [property: JsonPropertyName("jpegQuality"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? JpegQuality)
{
    public static RpcModelImageResizeOptions From(ModelImageResizeOptions options) =>
        new(options.MaxWidth, options.MaxHeight, options.MaxBytes, options.JpegQuality);
}
