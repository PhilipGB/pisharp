using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.EventStreams;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class BedrockProviderTests
{
    private static readonly byte[] s_png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/bX8AAAAASUVORK5CYII=");

    [Fact]
    public async Task BedrockProtocolMapsMafHistoryStreamsTextToolsReasoningAndUsage()
    {
        var model = BedrockModelCatalog.Load().Single(item => item.Id == "global.anthropic.claude-sonnet-4-6");
        var transport = new RecordingTransport([
            new MessageStartEvent { Role = ConversationRole.Assistant },
            new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new ContentBlockDelta { Text = "hello" } },
            new ContentBlockDeltaEvent { ContentBlockIndex = 1, Delta = new ContentBlockDelta
                { ReasoningContent = new ReasoningContentBlockDelta { Text = "thinking" } } },
            new ContentBlockDeltaEvent { ContentBlockIndex = 1, Delta = new ContentBlockDelta
                { ReasoningContent = new ReasoningContentBlockDelta { Signature = "QUJDRA==" } } },
            new ContentBlockStopEvent { ContentBlockIndex = 1 },
            new ContentBlockStartEvent { ContentBlockIndex = 2, Start = new ContentBlockStart
                { ToolUse = new ToolUseBlockStart { ToolUseId = "call_one", Name = "echo" } } },
            new ContentBlockDeltaEvent { ContentBlockIndex = 2, Delta = new ContentBlockDelta
                { ToolUse = new ToolUseBlockDelta { Input = "{\"text\":" } } },
            new ContentBlockDeltaEvent { ContentBlockIndex = 2, Delta = new ContentBlockDelta
                { ToolUse = new ToolUseBlockDelta { Input = "\"ping\"}" } } },
            new ContentBlockStopEvent { ContentBlockIndex = 2 },
            new ConverseStreamMetadataEvent { Usage = new TokenUsage
                { InputTokens = 40, OutputTokens = 15, CacheReadInputTokens = 5, CacheWriteInputTokens = 8, TotalTokens = 55 } },
            new MessageStopEvent { StopReason = "tool_use" }
        ]);
        using var client = new BedrockConverseChatClient(transport, model, enablePromptCaching: true,
            oneHourCache: false, forcePromptCaching: false, region: "us-east-1");
        var echo = AIFunctionFactory.Create((string text) => "echo:" + text, name: "echo");
        var history = new ChatMessage(ChatRole.User, [new TextContent("say hello"), new DataContent(s_png, "image/png")]);
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in client.GetStreamingResponseAsync([history], new ChatOptions
        {
            Instructions = "fixture system prompt",
            Temperature = 0.25f,
            MaxOutputTokens = 2048,
            Reasoning = ThinkingLevels.ToOptions("high"),
            Tools = [echo],
            ToolMode = ChatToolMode.RequireAny
        })) updates.Add(update);

        var request = Assert.IsType<ConverseStreamRequest>(transport.Request);
        Assert.Equal(model.Id, request.ModelId);
        Assert.Equal("fixture system prompt", request.System[0].Text);
        Assert.NotNull(request.System[1].CachePoint);
        Assert.Equal("say hello", request.Messages[0].Content[0].Text);
        Assert.Equal(ImageFormat.Png, request.Messages[0].Content[1].Image.Format);
        Assert.Equal(0.25f, request.InferenceConfig.Temperature);
        Assert.Equal(2048, request.InferenceConfig.MaxTokens);
        Assert.Single(request.ToolConfig.Tools);
        Assert.NotNull(request.ToolConfig.ToolChoice.Any);
        var reasoningFields = request.AdditionalModelRequestFields.AsDictionary();
        Assert.Equal("adaptive", reasoningFields["thinking"].AsDictionary()["type"].AsString());

        Assert.Contains(updates.SelectMany(update => update.Contents).OfType<TextContent>(), item => item.Text == "hello");
        var thinking = Assert.Single(updates.SelectMany(update => update.Contents).OfType<TextReasoningContent>());
        Assert.Equal("thinking", thinking.Text);
        Assert.Equal("QUJDRA==", thinking.ProtectedData);
        var call = Assert.Single(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>());
        Assert.Equal("call_one", call.CallId);
        Assert.Equal("ping", Assert.IsType<string>(call.Arguments!["text"]));
        Assert.Equal("tool_calls", updates[^1].FinishReason?.ToString());
        var usage = Assert.Single(updates.SelectMany(update => update.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(40, usage.InputTokenCount);
        Assert.Equal(15, usage.OutputTokenCount);
        Assert.Equal(5, usage.CachedInputTokenCount);
        Assert.Equal(55, usage.TotalTokenCount);
        Assert.Equal(8, usage.AdditionalCounts!["bedrock.cacheWriteInputTokenCount"]);
        Assert.True(transport.DisposedResponse);
    }

    [Fact]
    public void BedrockRequestCorrelatesToolResultsNormalizesIdsAndReplaysRedactedThinking()
    {
        var model = BedrockModelCatalog.Load().Single(item => item.Id == "global.anthropic.claude-sonnet-4-6");
        var redactedThinking = new TextReasoningContent("[Reasoning redacted]") { ProtectedData = "QUJDRA==" };
        BedrockRequestMapper.MarkRedactedReasoning(redactedThinking);
        ChatMessage[] history =
        [
            new(ChatRole.Assistant,
            [
                redactedThinking,
                new FunctionCallContent("call/one", "read", new Dictionary<string, object?> { ["path"] = "image.png" })
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("call/one", "read complete"), new DataContent(s_png, "image/png")]),
            new(ChatRole.Tool, [new FunctionResultContent("call/two", new { ok = true })])
        ];

        var request = BedrockRequestMapper.Build(model, history, null, enablePromptCaching: false,
            oneHourCache: false, forcePromptCaching: false, region: "us-east-1");

        Assert.Equal(2, request.Messages.Count);
        var assistant = request.Messages[0];
        Assert.Equal("call_one", assistant.Content[1].ToolUse.ToolUseId);
        var reasoning = assistant.Content[0].ReasoningContent;
        Assert.Equal(Convert.FromBase64String("QUJDRA=="), reasoning.RedactedContent.ToArray());
        var resultsMessage = request.Messages[1];
        Assert.Equal(ConversationRole.User, resultsMessage.Role);
        Assert.Equal(2, resultsMessage.Content.Count);
        Assert.Equal("call_one", resultsMessage.Content[0].ToolResult.ToolUseId);
        Assert.Equal("call_two", resultsMessage.Content[1].ToolResult.ToolUseId);
        Assert.Contains(resultsMessage.Content[0].ToolResult.Content, block => block.Image is not null);
    }

    [Fact]
    public async Task BedrockCancellationFlowsIntoAwsStreamTransport()
    {
        var model = BedrockModelCatalog.Load()[0];
        var transport = new RecordingTransport([], waitForCancellation: true);
        using var cancellation = new CancellationTokenSource();
        using var client = new BedrockConverseChatClient(transport, model, true, false, false, null);
        var enumerator = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")],
            cancellationToken: cancellation.Token).GetAsyncEnumerator();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        Assert.True(transport.CancellationObserved);
        await enumerator.DisposeAsync();
    }

    private sealed class RecordingTransport(IReadOnlyList<IEventStreamEvent> events,
        bool waitForCancellation = false) : IBedrockConverseTransport
    {
        public ConverseStreamRequest? Request { get; private set; }
        public bool DisposedResponse { get; private set; }
        public bool CancellationObserved { get; private set; }

        public Task<BedrockConverseStreamResponse> SendAsync(ConverseStreamRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            var stream = new CancellableEventStream(events, cancellationToken,
                () => CancellationObserved = true, waitForCancellation);
            return Task.FromResult(new BedrockConverseStreamResponse(stream, "bedrock-fixture-response",
                new CallbackDisposable(() => DisposedResponse = true)));
        }

        public void Dispose() { }
    }

    private sealed class CancellableEventStream(IReadOnlyList<IEventStreamEvent> events,
        CancellationToken requestToken, Action onCancellation, bool waitForCancellation) : IAsyncEnumerable<IEventStreamEvent>
    {
        public async IAsyncEnumerator<IEventStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestToken, cancellationToken);
            foreach (var item in events)
            {
                linked.Token.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
            if (!waitForCancellation) yield break;
            await Task.Delay(Timeout.Infinite, linked.Token).ContinueWith(_ => onCancellation(),
                CancellationToken.None, TaskContinuationOptions.OnlyOnCanceled, TaskScheduler.Default);
            linked.Token.ThrowIfCancellationRequested();
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }

}
