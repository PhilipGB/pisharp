using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class BedrockProviderTests
{
    [Fact]
    public async Task BedrockWireHooksTransformMarshalledRequestBeforeSigningAndObserveResponseBeforeStreamRead()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var wire = new ProviderWireTestSupport(attemptSigV4ReservedHeaderChanges: true);
        var eventBytes = Concat(
            EventFrame("messageStart", "{\"role\":\"assistant\"}"),
            EventFrame("contentBlockStart", "{\"contentBlockIndex\":0,\"start\":{\"text\":\"\"}}"),
            EventFrame("contentBlockDelta", "{\"contentBlockIndex\":0,\"delta\":{\"text\":\"bedrock wire\"}}"),
            EventFrame("contentBlockStop", "{\"contentBlockIndex\":0}"),
            EventFrame("messageStop", "{\"stopReason\":\"end_turn\"}"));
        var handler = new BedrockWireHttpHandler(wire, eventBytes);

        var config = new AmazonBedrockRuntimeConfig
        {
            ServiceURL = "http://bedrock-wire-fixture",
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
            HttpClientFactory = new BedrockWireHttpClientFactory(handler)
        };
        var runtime = new AmazonBedrockRuntimeClient(new BasicAWSCredentials("fixture-access", "fixture-secret"), config);
        BedrockProviderWirePipelineCustomizer.Install(runtime);
        var model = BedrockModelCatalog.Load().Single(item => item.Id == "global.anthropic.claude-sonnet-4-6");
        using var client = new BedrockConverseChatClient(new AwsBedrockConverseTransport(runtime), model,
            enablePromptCaching: false, oneHourCache: false, forcePromptCaching: false, region: "us-east-1");
        using var wireScope = wire.Enter(deadline.Token);
        var output = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hello bedrock")], cancellationToken: deadline.Token))
            output.Append(update.Text);

        Assert.Equal("bedrock wire", output.ToString());
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(sent.RootElement.TryGetProperty("wire_probe", out var wireProbe) && wireProbe.GetBoolean(),
            $"Bedrock request payload was not transformed. Body: {handler.RequestBody}. Hooks: {string.Join(",", wire.Events)}");
        Assert.Equal("transformed", handler.HookHeader);
        Assert.StartsWith("AWS4-HMAC-SHA256", handler.Authorization, StringComparison.Ordinal);
        Assert.Contains("x-pisharp-wire-probe", handler.Authorization, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("forged.example", handler.Host);
        Assert.NotEqual("forged-date", handler.AmzDate);
        wire.AssertHooks("bedrock-converse-stream", responseExposed: true);
        Assert.Equal("amazon-bedrock", Assert.Single(wire.ProviderIds.Distinct()));
    }

    [Theory]
    [InlineData("""{"text":"\url{x}","mode":"w"}""", "\\url{x}")]
    [InlineData("""{"text":"ab\u00""", null)]
    [InlineData("""{"text":"keep", BROKEN}""", null)]
    public async Task BedrockToolArgumentsRepairStringEscapesAndRejectMalformedCompletedCalls(string arguments, string? expected)
    {
        var transport = new RecordingTransport([
            new MessageStartEvent { Role = ConversationRole.Assistant },
            new ContentBlockStartEvent { ContentBlockIndex = 0, Start = new ContentBlockStart
                { ToolUse = new ToolUseBlockStart { ToolUseId = "call_fixture", Name = "fixture_tool" } } },
            new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new ContentBlockDelta
                { ToolUse = new ToolUseBlockDelta { Input = arguments } } },
            new ContentBlockStopEvent { ContentBlockIndex = 0 },
            new MessageStopEvent { StopReason = "tool_use" }
        ]);
        using var client = new BedrockConverseChatClient(transport,
            BedrockModelCatalog.Load().Single(item => item.Id == "global.anthropic.claude-sonnet-4-6"), false, false, false, "us-east-1");
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "fixture")]);
        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        if (expected is null)
        {
            Assert.Empty(call.Arguments!);
            Assert.Contains(arguments, Assert.IsType<System.Text.Json.JsonException>(call.Exception).Message);
        }
        else
        {
            Assert.Null(call.Exception);
            Assert.Equal(expected, call.Arguments!["text"]);
        }
    }

    [Fact]
    public async Task BedrockMalformedToolCallIsRejectedBeforeValidToolContinuation()
    {
        var transport = new SequenceTransport(
            ToolCallEvents("call_invalid", "{\"path\":\"prefix.txt\",\"content\":\"bad\", BROKEN}"),
            ToolCallEvents("call_valid", "{\"path\":\"continued.txt\",\"content\":\"valid continuation\"}"),
            [new MessageStartEvent { Role = ConversationRole.Assistant },
                new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new ContentBlockDelta { Text = "completed" } },
                new MessageStopEvent { StopReason = "end_turn" }]);
        var model = BedrockModelCatalog.Load().Single(item => item.Id == "global.anthropic.claude-sonnet-4-6");
        using var client = new BedrockConverseChatClient(transport, model, false, false, false, "us-east-1");
        var invocations = new List<(string Path, string Content)>();
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                Tools = [AIFunctionFactory.Create((string path, string content) =>
                {
                    invocations.Add((path, content));
                    return $"wrote {path}";
                }, name: "fixture_tool")]
            }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await agent.RunAsync("continue after the malformed tool call", cancellationToken: deadline.Token);

        Assert.Equal([("continued.txt", "valid continuation")], invocations);
        Assert.Equal(3, transport.Requests.Count);
        var failedResult = transport.Requests[1].Messages.SelectMany(message => message.Content)
            .Select(content => content.ToolResult).Single(result => result?.ToolUseId == "call_invalid");
        Assert.Equal(ToolResultStatus.Error, failedResult!.Status);
        var successfulResult = transport.Requests[2].Messages.SelectMany(message => message.Content)
            .Select(content => content.ToolResult).Single(result => result?.ToolUseId == "call_valid");
        Assert.Equal(ToolResultStatus.Success, successfulResult!.Status);
        Assert.Contains(successfulResult.Content, item => item.Text == "wrote continued.txt");
    }

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
        Assert.Equal(45, usage.InputTokenCount);
        Assert.Equal(15, usage.OutputTokenCount);
        Assert.Equal(5, usage.CachedInputTokenCount);
        Assert.Equal(68, usage.TotalTokenCount);
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
    public void BedrockLegacyClaudeExpandsOutputCeilingForConfiguredThinkingBudget()
    {
        var model = new ModelDescriptor("anthropic.claude-3-7-sonnet-20250219-v1:0", "Anthropic",
            128_000, "configured", Reasoning: true, Name: "Claude 3.7 Sonnet", MaxOutputTokens: 20_000);
        var options = new ChatOptions
        {
            MaxOutputTokens = 4096,
            Reasoning = ThinkingLevels.ToOptions("medium")
        };
        options.AdditionalProperties ??= new();
        options.AdditionalProperties[ThinkingBudgetPolicy.SettingsProperty] =
            new Dictionary<string, int>(StringComparer.Ordinal) { ["medium"] = 5000 };

        var request = BedrockRequestMapper.Build(model, [new ChatMessage(ChatRole.User, "hello")], options,
            enablePromptCaching: false, oneHourCache: false, forcePromptCaching: false, region: "us-east-1");

        Assert.Equal(9096, request.InferenceConfig.MaxTokens);
        var thinking = request.AdditionalModelRequestFields.AsDictionary()["thinking"].AsDictionary();
        Assert.Equal(5000, thinking["budget_tokens"].AsInt());
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

    private static byte[] EventFrame(string eventType, string payload)
    {
        var eventHeader = new EventStreamHeader(":event-type");
        eventHeader.SetString(eventType);
        var messageHeader = new EventStreamHeader(":message-type");
        messageHeader.SetString("event");
        var contentHeader = new EventStreamHeader(":content-type");
        contentHeader.SetString("application/json");
        return new EventStreamMessage([eventHeader, messageHeader, contentHeader], Encoding.UTF8.GetBytes(payload))
            .ToByteArray();
    }

    private static byte[] Concat(params byte[][] buffers)
    {
        var result = new byte[buffers.Sum(buffer => buffer.Length)];
        var offset = 0;
        foreach (var buffer in buffers)
        {
            buffer.CopyTo(result, offset);
            offset += buffer.Length;
        }
        return result;
    }

    private sealed class BedrockWireHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig config) => new(handler, disposeHandler: false);
        public override bool UseSDKHttpClientCaching(IClientConfig config) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig config) => true;
    }

    private sealed class BedrockWireHttpHandler(ProviderWireTestSupport wire, byte[] responseBytes)
        : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string? HookHeader { get; private set; }
        public string? Authorization { get; private set; }
        public string? Host { get; private set; }
        public string? AmzDate { get; private set; }
        public string RequestHeaders { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            HookHeader = request.Headers.TryGetValues("x-pisharp-wire-probe", out var values)
                ? values.SingleOrDefault() : null;
            Authorization = request.Headers.TryGetValues("Authorization", out var authValues)
                ? string.Join(", ", authValues) : null;
            Host = request.Headers.Host;
            AmzDate = request.Headers.TryGetValues("x-amz-date", out var dateValues)
                ? string.Join(", ", dateValues) : null;
            RequestHeaders = request.Headers.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(new BedrockWireGateStream(responseBytes, wire.ResponseObserved.Task))
            };
            response.Headers.TryAddWithoutValidation("x-amzn-requestid", "wire-fixture-request");
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "application/vnd.amazon.eventstream");
            return response;
        }
    }

    private sealed class BedrockWireGateStream(byte[] bytes, Task responseObserved) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await responseObserved.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            await responseObserved.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            responseObserved.GetAwaiter().GetResult();
            return base.Read(buffer, offset, count);
        }
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

    private static IReadOnlyList<IEventStreamEvent> ToolCallEvents(string id, string arguments) =>
    [
        new MessageStartEvent { Role = ConversationRole.Assistant },
        new ContentBlockStartEvent { ContentBlockIndex = 0, Start = new ContentBlockStart
            { ToolUse = new ToolUseBlockStart { ToolUseId = id, Name = "fixture_tool" } } },
        new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new ContentBlockDelta
            { ToolUse = new ToolUseBlockDelta { Input = arguments } } },
        new ContentBlockStopEvent { ContentBlockIndex = 0 },
        new MessageStopEvent { StopReason = "tool_use" }
    ];

    private sealed class SequenceTransport(params IReadOnlyList<IEventStreamEvent>[] responses) : IBedrockConverseTransport
    {
        private int _index;
        public List<ConverseStreamRequest> Requests { get; } = [];

        public Task<BedrockConverseStreamResponse> SendAsync(ConverseStreamRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_index >= responses.Length)
                throw new InvalidOperationException("The Bedrock fixture has no response for this request.");
            var events = responses[_index++];
            return Task.FromResult(new BedrockConverseStreamResponse(
                new CancellableEventStream(events, cancellationToken, static () => { }, waitForCancellation: false),
                $"bedrock-fixture-{_index}"));
        }

        public void Dispose() { }
    }

}
