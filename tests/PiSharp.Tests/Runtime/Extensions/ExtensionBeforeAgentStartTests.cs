using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class ExtensionBeforeAgentStartTests
{
    [Fact]
    public async Task BeforeAgentStartHandlersChainTurnPromptAndIgnoreHandlerFailure()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-before-agent-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var registration = new ExtensionRegistration();
            var order = new List<string>();
            PiSharpExtensionContext? observedContext = null;
            registration.AddBeforeAgentStartHandler((eventData, context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal("hello", eventData.Prompt);
                Assert.Single(eventData.Images);
                Assert.Equal("image/png", eventData.Images[0].MediaType);
                Assert.Contains("base prompt", eventData.SystemPrompt, StringComparison.Ordinal);
                Assert.Contains("mcp context", eventData.SystemPrompt, StringComparison.Ordinal);
                Assert.Equal(workingDirectory, context.WorkingDirectory);
                Assert.Equal(conversation.Id, context.SessionId);
                Assert.False(context.IsIdle);
                observedContext = context;
                order.Add("first");
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(
                    new(eventData.SystemPrompt + "\nfirst"));
            });
            registration.AddBeforeAgentStartHandler((eventData, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.EndsWith("first", eventData.SystemPrompt, StringComparison.Ordinal);
                order.Add("second");
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(
                    new(eventData.SystemPrompt + "\nsecond"));
            });
            registration.AddBeforeAgentStartHandler((_, _, _) =>
                throw new InvalidOperationException("extension setup failed"));

            var provider = new BeforeAgentStartClient();
            var agent = new PiAgent(provider, new CodingTools(workingDirectory), systemPrompt: "base prompt",
                getAdditionalSystemInstructions: () => "mcp context",
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers);
            var run = await ConversationRun.OpenAsync(agent, conversation);
            var events = new List<AgentLifecycleEvent>();

            var prompt = new ChatMessage(ChatRole.User,
                [new TextContent("hello"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
            await foreach (var item in run.RunEventsAsync(prompt, "hello")) events.Add(item);

            Assert.NotNull(observedContext);
            Assert.True(observedContext.IsIdle);
            Assert.Equal(["first", "second"], order);
            var error = Assert.Single(events, item => item.Type == "extension_error");
            Assert.Equal("extension setup failed", error.Error);
            var instructions = Assert.Single(provider.Instructions);
            Assert.Contains("base prompt", instructions, StringComparison.Ordinal);
            Assert.Contains("first\nsecond", instructions, StringComparison.Ordinal);
            Assert.Equal(1, instructions.Split("mcp context", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("first", agent.SystemInstructions, StringComparison.Ordinal);
            Assert.Contains(run.Conversation.ContextMessages(), message =>
                message.Role == ChatRole.User && message.Text == "hello");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class BeforeAgentStartClient : IChatClient
    {
        public List<string> Instructions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Instructions.Add(options?.Instructions ?? "");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
