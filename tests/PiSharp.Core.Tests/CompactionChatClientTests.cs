using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

/// <summary>
/// Direct tests of the provider-request compaction seam: threshold compaction before the
/// request, forced compaction on provider overflow, and error pass-through. The MAF chat
/// history attribution stamps are applied exactly as the per-service-call persistence
/// middleware does, so history/in-flight splitting is exercised faithfully.
/// </summary>
public sealed class CompactionChatClientTests
{
    [Fact]
    public async Task ThresholdCompactionRebuildsRequestFromEffectiveHistory()
    {
        var compactor = new FakeCompactor
        {
            EnsureResult = true,
            History = [new ChatMessage(ChatRole.User, [new TextContent("compacted context")])],
        };
        var leaf = new RecordingLeafClient([FinalBehavior("ok")]);
        var client = new CompactionChatClient(leaf, () => compactor);

        var request = Request(
            History("h1"),
            History("h2"),
            External("prompt"));

        await client.GetResponseAsync(request, null, CancellationToken.None);

        Assert.Equal(1, compactor.EnsureCalls);
        Assert.Equal(1, leaf.CallCount);
        var sent = leaf.Requests[0].Select(ReadText).ToArray();
        Assert.Equal(new[] { "compacted context", "prompt" }, sent);
    }

    [Fact]
    public async Task NoCompactionPassesRequestThroughUnchanged()
    {
        var compactor = new FakeCompactor { EnsureResult = false };
        var leaf = new RecordingLeafClient([FinalBehavior("ok")]);
        var client = new CompactionChatClient(leaf, () => compactor);

        var request = Request(History("h1"), External("prompt"));
        var response = await client.GetResponseAsync(request, null, CancellationToken.None);

        Assert.Equal(1, compactor.EnsureCalls);
        Assert.Equal("ok", response.Text);
        var sent = leaf.Requests[0].Select(ReadText).ToArray();
        Assert.Equal(new[] { "h1", "prompt" }, sent);
    }

    [Fact]
    public async Task OverflowBeforeContentForcesOneCompactionAndRetriesTheRequest()
    {
        var compactor = new FakeCompactor
        {
            ForceResult = true,
            History = [new ChatMessage(ChatRole.User, [new TextContent("compacted context")])],
        };
        var leaf = new RecordingLeafClient([
            new CompactionRuntimeTests.LeafBehavior
            {
                Throw = new InvalidOperationException("prompt is too long: 200000 > 128000"),
            },
            new CompactionRuntimeTests.LeafBehavior { BuildUpdates = _ => FinalUpdates("recovered") },
        ]);
        var client = new CompactionChatClient(leaf, () => compactor);

        var response = await client.GetResponseAsync(Request(History("h1"), External("prompt")), null, CancellationToken.None);

        Assert.Equal("recovered", response.Text);
        Assert.Equal(1, compactor.ForceCalls);
        Assert.Equal(2, leaf.CallCount);
        // The retry carries the compacted history and the same in-flight prompt — no replay.
        var retried = leaf.Requests[1].Select(ReadText).ToArray();
        Assert.Equal(new[] { "compacted context", "prompt" }, retried);
    }

    [Fact]
    public async Task StreamingOverflowRecoveryStreamsTheRetriedResponse()
    {
        var compactor = new FakeCompactor
        {
            ForceResult = true,
            History = [new ChatMessage(ChatRole.User, [new TextContent("compacted context")])],
        };
        var leaf = new RecordingLeafClient([
            new CompactionRuntimeTests.LeafBehavior
            {
                Throw = new InvalidOperationException("exceeds the available context size"),
            },
            new CompactionRuntimeTests.LeafBehavior { BuildUpdates = _ => FinalUpdates("streamed") },
        ]);
        var client = new CompactionChatClient(leaf, () => compactor);

        var updates = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync(Request(History("h1"), External("prompt")), null, CancellationToken.None))
        {
            updates.Add(update.Text);
        }

        Assert.Equal("streamed", string.Concat(updates));
        Assert.Equal(1, compactor.ForceCalls);
        Assert.Equal(2, leaf.CallCount);
        var retried = leaf.Requests[1].Select(ReadText).ToArray();
        Assert.Equal(new[] { "compacted context", "prompt" }, retried);
    }

    [Fact]
    public async Task SecondOverflowAfterRecoverySurfacesInsteadOfLooping()
    {
        var compactor = new FakeCompactor { ForceResult = true, History = [new ChatMessage(ChatRole.User, [new TextContent("s")])] };
        var leaf = new RecordingLeafClient([
            new CompactionRuntimeTests.LeafBehavior
            {
                Throw = new InvalidOperationException("prompt is too long"),
            },
            new CompactionRuntimeTests.LeafBehavior
            {
                Throw = new InvalidOperationException("prompt is too long"),
            },
        ]);
        var client = new CompactionChatClient(leaf, () => compactor);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync(Request(History("h1"), External("prompt")), null, CancellationToken.None));

        Assert.Equal(1, compactor.ForceCalls);
        Assert.Equal(2, leaf.CallCount);
    }

    [Fact]
    public async Task NonOverflowErrorPropagatesWithoutCompaction()
    {
        var compactor = new FakeCompactor { ForceResult = true };
        var leaf = new RecordingLeafClient([
            new CompactionRuntimeTests.LeafBehavior
            {
                Throw = new InvalidOperationException("401 Unauthorized: invalid api key"),
            },
        ]);
        var client = new CompactionChatClient(leaf, () => compactor);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync(Request(History("h1"), External("prompt")), null, CancellationToken.None));

        Assert.Equal(0, compactor.ForceCalls);
        Assert.Equal(1, compactor.EnsureCalls); // threshold check ran; no recovery was attempted
        Assert.Equal(1, leaf.CallCount);
    }

    [Fact]
    public async Task CancellationIsNotMisclassifiedAsOverflow()
    {
        var compactor = new FakeCompactor { ForceResult = true };
        var leaf = new RecordingLeafClient([
            new CompactionRuntimeTests.LeafBehavior { Throw = new OperationCanceledException() },
        ]);
        var client = new CompactionChatClient(leaf, () => compactor);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.GetResponseAsync(Request(History("h1"), External("prompt")), null, CancellationToken.None));

        Assert.Equal(0, compactor.ForceCalls);
        Assert.Equal(1, leaf.CallCount);
    }

    [Fact]
    public async Task NoCompactorPassesThroughEvenOnOverflow()
    {
        var leaf = new RecordingLeafClient([
            new CompactionRuntimeTests.LeafBehavior
            {
                Throw = new InvalidOperationException("prompt is too long"),
            },
        ]);
        var client = new CompactionChatClient(leaf, () => null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync(Request(History("h1"), External("prompt")), null, CancellationToken.None));

        Assert.Equal(1, leaf.CallCount);
    }

    private static List<ChatMessage> Request(params ChatMessage[] messages) => [.. messages];

    private static ChatMessage History(string text) =>
        Message(text).WithAgentRequestMessageSource(AgentRequestMessageSourceType.ChatHistory);

    private static ChatMessage External(string text) => Message(text);

    private static ChatMessage Message(string text) =>
        new(ChatRole.User, new AIContent[] { new TextContent(text) });

    private static CompactionRuntimeTests.LeafBehavior FinalBehavior(string text) =>
        new() { BuildUpdates = _ => FinalUpdates(text) };

    private static List<ChatResponseUpdate> FinalUpdates(string text) =>
    [
        new(ChatRole.Assistant, new AIContent[] { new TextContent(text) }) { FinishReason = ChatFinishReason.Stop },
    ];

    private static string ReadText(ChatMessage message) =>
        string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));

    /// <summary>Records every request reaching the inner client.</summary>
    internal sealed class RecordingLeafClient : IChatClient
    {
        private readonly object _sync = new();
        private readonly List<IReadOnlyList<ChatMessage>> _requests = [];
        private readonly Queue<CompactionRuntimeTests.LeafBehavior> _script;
        private int _calls;

        public RecordingLeafClient(IReadOnlyList<CompactionRuntimeTests.LeafBehavior> script)
        {
            _script = new Queue<CompactionRuntimeTests.LeafBehavior>(script);
        }

        public int CallCount
        {
            get
            {
                lock (_sync)
                {
                    return _calls;
                }
            }
        }

        public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests
        {
            get
            {
                lock (_sync)
                {
                    return _requests.ToArray();
                }
            }
        }

        private CompactionRuntimeTests.LeafBehavior Next()
        {
            lock (_sync)
            {
                _calls++;
                return _script.Count > 1 ? _script.Dequeue() : _script.Peek();
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Record(messages);
            var behavior = Next();
            if (behavior.Throw is not null)
            {
                return Task.FromException<ChatResponse>(behavior.Throw);
            }

            var list = (messages as IReadOnlyList<ChatMessage>) ?? messages.ToArray();
            var contents = behavior.BuildUpdates!(list).SelectMany(update => update.Contents).ToArray();
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, contents)]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            StreamAsync(messages, cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            IEnumerable<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Record(messages);
            var behavior = Next();
            if (behavior.Throw is not null)
            {
                throw behavior.Throw;
            }

            var list = (messages as IReadOnlyList<ChatMessage>) ?? messages.ToArray();
            foreach (var update in behavior.BuildUpdates!(list))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        private void Record(IEnumerable<ChatMessage> messages)
        {
            lock (_sync)
            {
                _requests.Add(messages as IReadOnlyList<ChatMessage> ?? messages.ToArray());
            }
        }

        public IChatClient WithInstructions(string? instructions) => this;

        public IChatClient WithTools(IEnumerable<AITool> tools) => this;

        public IChatClient WithTools(params AIFunction[] functions) => this;

        public IChatClient WithFunctions(params AIFunction[] functions) => this;

        public IChatClient WithFunctions(IEnumerable<AIFunction> functions) => this;

        public object? GetService(Type serviceType, object? serviceKey) => null;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Scripted compaction authority recording which decisions were requested.</summary>
    internal sealed class FakeCompactor : IProviderRequestCompactor
    {
        private int _ensureCalls;
        private int _forceCalls;

        public bool EnsureResult { get; init; }

        public bool ForceResult { get; init; }

        public IReadOnlyList<ChatMessage> History { get; init; } = [];

        public int EnsureCalls
        {
            get
            {
                lock (this)
                {
                    return _ensureCalls;
                }
            }
        }

        public int ForceCalls
        {
            get
            {
                lock (this)
                {
                    return _forceCalls;
                }
            }
        }

        public Task<bool> EnsureContextFitsAsync(CompactionReason trigger, CancellationToken cancellationToken)
        {
            lock (this)
            {
                _ensureCalls++;
            }
            return Task.FromResult(EnsureResult);
        }

        public Task<bool> ForceCompactAsync(CancellationToken cancellationToken)
        {
            lock (this)
            {
                _forceCalls++;
            }
            return Task.FromResult(ForceResult);
        }

        public IReadOnlyList<ChatMessage> GetEffectiveHistory() => History;
    }
}
