using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NativeExtensions = PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class NativeExtensionHookBridgeTests
{
    [Fact]
    public async Task PublicBeforeAgentStartContextCapturesCurrentTrustIdleAndPendingState()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            NativeExtensions.PiSharpExtensionContext? observedContext = null;
            adapter.AddBeforeAgentStartHandler((_, context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                observedContext = context;
                return ValueTask.FromResult<NativeExtensions.PiSharpBeforeAgentStartResult?>(null);
            });
            adapter.Close();

            var agent = new PiAgent(new HookBridgeClient(), new CodingTools(cwd), noBuiltinTools: true,
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers,
                extensionContextConfiguration: new PiSharpExtensionContextConfiguration(
                    cwd, "print", false, true, new TestExtensionUi()));
            var run = await ConversationRun.OpenAsync(agent, new ConversationSession(cwd, "test-model", null));

            await foreach (var _ in run.RunEventsAsync("context check")) { }

            Assert.NotNull(observedContext);
            Assert.Equal(cwd, observedContext.WorkingDirectory);
            Assert.False(observedContext.IsIdle);
            Assert.True(observedContext.IsProjectTrusted);
            Assert.False(observedContext.HasPendingMessages);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task PublicContextTransformsComposePerRequestWithoutChangingConversationHistory()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var order = new List<string>();
            adapter.AddContextTransform((messages, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("first");
                var transformed = messages.Select(message => message.Clone()).ToArray();
                AppendToLastUserMessage(transformed, " [first]");
                return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(transformed);
            });
            adapter.AddContextTransform((messages, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("second");
                Assert.Contains("[first]", messages.Last(message => message.Role == ChatRole.User).Text);
                var transformed = messages.Select(message => message.Clone()).ToArray();
                AppendToLastUserMessage(transformed, " [second]");
                return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(transformed);
            });
            adapter.Close();

            var provider = new HookBridgeClient();
            var run = await ConversationRun.OpenAsync(
                new PiAgent(provider, new CodingTools(cwd), noBuiltinTools: true,
                    extensionContextTransforms: registration.ContextTransforms),
                new ConversationSession(cwd, "test-model", null));

            await foreach (var _ in run.RunEventsAsync("prompt")) { }

            Assert.Equal(["first", "second"], order);
            var request = Assert.Single(provider.Requests);
            var providerPrompt = Assert.Single(request, message => message.Role == ChatRole.User);
            Assert.Contains("[first]", providerPrompt.Text);
            Assert.Contains("[second]", providerPrompt.Text);
            Assert.Contains(run.Conversation.ContextMessages(), message =>
                message.Role == ChatRole.User && message.Text == "prompt");
            Assert.DoesNotContain(run.Conversation.ContextMessages(), message =>
                message.Text.Contains("[first]", StringComparison.Ordinal) ||
                message.Text.Contains("[second]", StringComparison.Ordinal));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task PublicToolHooksMutateArgumentsAndComposeResultTransformationsInOrder()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var order = new List<string>();
            string? resultSeenByFirstHook = null;
            string? resultSeenBySecondHook = null;
            var resultWasError = true;
            adapter.AddToolCallHook((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("call-first");
                Assert.Equal("call-id", context.ToolCallId);
                Assert.Equal("echo", context.ToolName);
                context.Arguments["value"] = "first";
                return ValueTask.FromResult(NativeExtensions.PiSharpToolCallDecision.Allow);
            });
            adapter.AddToolCallHook((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("call-second");
                Assert.Equal("first", context.Arguments["value"]);
                context.Arguments["value"] = "second";
                return ValueTask.FromResult(NativeExtensions.PiSharpToolCallDecision.Allow);
            });
            adapter.AddToolResultHook((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("result-first");
                resultWasError = context.IsError;
                resultSeenByFirstHook = context.Result?.ToString();
                context.Result = "first:" + context.Result;
                return ValueTask.CompletedTask;
            });
            adapter.AddToolResultHook((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("result-second");
                resultSeenBySecondHook = context.Result?.ToString();
                context.Result = "second:" + context.Result;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var tool = AIFunctionFactory.Create((string value) => "echo:" + value, name: "echo");
            var provider = new HookBridgeClient("echo", "call-id");
            var agent = new PiAgent(provider, new CodingTools(cwd), noBuiltinTools: true,
                extensionToolRegistrations: [new(tool, ToolExposure.ModelOnly)],
                extensionToolCallHooks: registration.ToolCallHooks,
                extensionToolResultHooks: registration.ToolResultHooks);
            var run = await ConversationRun.OpenAsync(agent, new ConversationSession(cwd, "test-model", null));

            await foreach (var _ in run.RunEventsAsync("call echo")) { }

            Assert.Equal(["call-first", "call-second", "result-first", "result-second"], order);
            Assert.False(resultWasError);
            Assert.Equal("echo:second", resultSeenByFirstHook);
            Assert.Equal("first:echo:second", resultSeenBySecondHook);
            Assert.Equal("second:first:echo:second", provider.ToolResult);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task PublicToolCallHookCanBlockExecutionAndResultHookSeesTheBlockedError()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var invocations = 0;
            string? blockedError = null;
            adapter.AddToolCallHook((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal("blocked-call", context.ToolCallId);
                return ValueTask.FromResult(NativeExtensions.PiSharpToolCallDecision.Block("policy blocked"));
            });
            adapter.AddToolResultHook((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.True(context.IsError);
                blockedError = context.Error;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var tool = AIFunctionFactory.Create(() =>
            {
                invocations++;
                return "should not run";
            }, name: "protected");
            var provider = new HookBridgeClient("protected", "blocked-call");
            var agent = new PiAgent(provider, new CodingTools(cwd), noBuiltinTools: true,
                extensionToolRegistrations: [new(tool, ToolExposure.ModelOnly)],
                extensionToolCallHooks: registration.ToolCallHooks,
                extensionToolResultHooks: registration.ToolResultHooks);
            var run = await ConversationRun.OpenAsync(agent, new ConversationSession(cwd, "test-model", null));
            var events = new List<AgentLifecycleEvent>();

            await foreach (var item in run.RunEventsAsync("invoke protected")) events.Add(item);

            Assert.Equal(0, invocations);
            Assert.Equal("policy blocked", blockedError);
            Assert.Contains(events, item => item.Type == "tool_execution_finished" && item.IsError == true &&
                item.Error == "policy blocked");
            Assert.Equal(2, provider.Requests.Count);
            Assert.False(string.IsNullOrWhiteSpace(provider.ToolResult));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    private static string NewWorkingDirectory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-public-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        return cwd;
    }

    private static void AppendToLastUserMessage(IReadOnlyList<ChatMessage> messages, string suffix)
    {
        var message = messages.Last(item => item.Role == ChatRole.User);
        message.Contents.OfType<TextContent>().Last().Text += suffix;
    }

    private sealed class HookBridgeClient(string? toolName = null, string? toolCallId = null) : IChatClient
    {
        private int _requests;

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public string? ToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = messages.Select(message => message.Clone()).ToArray();
            Requests.Add(request);
            if (Interlocked.Increment(ref _requests) == 1 && toolName is not null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent(toolCallId!, toolName,
                        new Dictionary<string, object?> { [toolName == "echo" ? "value" : "unused"] = "original" })]);
            }
            else
            {
                var result = request.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>().SingleOrDefault();
                ToolResult = result?.Result?.ToString() ?? result?.Exception?.Message;
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestExtensionUi : IPiSharpExtensionUi
    {
        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<string?> InputAsync(string title, string? placeholder = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public void Notify(string message, string type = "info") { }
    }
}
