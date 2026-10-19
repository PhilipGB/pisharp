using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeSettlement;

public sealed class SettlementExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        var continuedTurn = false;
        var continuedSettlement = false;
        builder.AddProvider(new PiSharpExtensionProviderDefinition("settlement_fixture", "Settlement fixture",
            [new("normal", "Normal"), new("retry", "Retry"), new("tools", "Tools")],
            context => new SettlementClient(context.ModelId))
        { AuthRequired = false });
        builder.AddTool(AIFunctionFactory.Create(() => "package tool result", name: "settlement_probe"));
        builder.AddMessageStartHandler((data, _, _) =>
        {
            Record("message-start:" + data.Message.Role);
            return ValueTask.CompletedTask;
        });
        builder.AddMessageUpdateHandler((data, _, _) =>
        {
            Record("message-update:" + data.UpdateType);
            return ValueTask.CompletedTask;
        });
        builder.AddMessageEndHandler((data, _, _) =>
        {
            Record("message-end:" + data.Message.Role);
            return ValueTask.FromResult<PiSharpExtensionMessageEndResult?>(null);
        });
        builder.AddAgentStartHandler((_, _, _) => { Record("agent-start"); return ValueTask.CompletedTask; });
        builder.AddTurnStartHandler((data, _, _) =>
        {
            Record("turn-start:" + data.TurnIndex);
            return ValueTask.CompletedTask;
        });
        builder.AddTurnEndHandler((data, _, _) =>
        {
            Record($"legacy-turn:{data.TurnIndex}:{data.MessageEntryId}:{data.ToolResultEntryIds.Count}");
            return ValueTask.CompletedTask;
        });
        builder.AddTurnEndBoundaryHandler((data, _, _) =>
        {
            Record($"mutable-turn:{data.Turn.TurnIndex}:{data.Outcome}:{data.Turn.ToolResultEntryIds.Count}");
            if (continuedTurn || data.Outcome != PiSharpExtensionAgentOutcome.Completed ||
                data.Turn.Message?.Contents.OfType<FunctionCallContent>().Any() == true)
                return ValueTask.FromResult<PiSharpBoundaryResult?>(null);
            continuedTurn = true;
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new([
                new PiSharpCustomMessageEntryDraft("settlement-context", "boundary package context", false,
                    JsonSerializer.SerializeToElement(new { origin = "package" }))
            ], true));
        });
        builder.AddTurnEndBoundaryHandler((data, _, _) =>
        {
            if (data.Context.ContextMessages.Any(message => message.Text == "boundary package context"))
                Record("later-handler-sees-context");
            return ValueTask.FromResult<PiSharpBoundaryResult?>(null);
        });
        builder.AddAgentEndHandler((_, _, _) => { Record("agent-end"); return ValueTask.CompletedTask; });
        builder.AddAgentBeforeSettleHandler((data, _, _) =>
        {
            Record("before-settle:" + data.Outcome);
            if (!continuedSettlement && Environment.GetEnvironmentVariable("SETTLEMENT_CONTINUE") == "1")
            {
                continuedSettlement = true;
                return ValueTask.FromResult<PiSharpBoundaryResult?>(new([
                    new PiSharpCustomMessageEntryDraft("settlement-next", "before settlement context", false)
                ], true));
            }
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new([
                new PiSharpCustomEntryDraft("settlement-receipt", JsonSerializer.SerializeToElement(new { done = true }))
            ], false));
        });
        builder.AddAgentSettledHandler((data, _, _) =>
        {
            Record("settled:" + data.Aborted);
            return ValueTask.CompletedTask;
        });
        builder.AddSessionStartHandler((_, context, _) =>
        {
            var state = context.Session?.Snapshot.Entries.Count(entry => entry.CustomType == "settlement-receipt") ?? 0;
            Record("resumed-receipts:" + state);
            return ValueTask.CompletedTask;
        });
    }

    internal static void Record(string value)
    {
        if (Environment.GetEnvironmentVariable("SETTLEMENT_LOG") is { } path)
            File.AppendAllText(path, value + Environment.NewLine);
    }
}

internal sealed class SettlementClient(string model) : IChatClient
{
    private int _requests;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = Interlocked.Increment(ref _requests);
        var context = messages.ToArray();
        SettlementExtension.Record($"request:{request}:{model}:boundary=" +
            context.Any(message => message.Text == "boundary package context") + ":settlement=" +
            context.Any(message => message.Text == "before settlement context"));
        if (model == "retry" && request == 1) throw new HttpRequestException("503 service unavailable");
        var hasToolResult = context.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any();
        var response = model == "tools" && !hasToolResult
            ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("package-tool", "settlement_probe")])
            : new ChatMessage(ChatRole.Assistant, "package provider response");
        return Task.FromResult(new ChatResponse(response) { ModelId = model });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
