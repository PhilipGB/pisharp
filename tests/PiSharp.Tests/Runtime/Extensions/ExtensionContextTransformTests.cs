using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class ExtensionContextTransformTests
{
    [Fact]
    public async Task TransformsRunInOrderForEachProviderRequestWithoutChangingConversationHistory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-context-transform-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var registration = new ExtensionRegistration();
            var order = new List<string>();
            registration.AddContextTransform((messages, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("first");
                var transformed = messages.Select(message => message.Clone()).ToList();
                AppendToLastUserMessage(transformed, " [first]");
                foreach (var call in transformed.SelectMany(message => message.Contents).OfType<FunctionCallContent>())
                    call.Arguments!["value"] = "provider-only";
                return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(transformed);
            });
            registration.AddContextTransform((messages, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("second");
                Assert.Contains("[first]", messages.Last(message => message.Role == ChatRole.User).Text);
                var transformed = messages.Select(message => message.Clone()).ToList();
                AppendToLastUserMessage(transformed, " [second]");
                return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(transformed);
            });

            var provider = new ContextTransformClient();
            var echo = AIFunctionFactory.Create((string value) => "echo: " + value, name: "echo_ext");
            var agent = new PiAgent(provider, new CodingTools(cwd), selectedTools: ["echo_ext"],
                extensionTools: [echo], extensionContextTransforms: registration.ContextTransforms);
            var conversation = new ConversationSession(cwd, "test-model", null);
            var run = await ConversationRun.OpenAsync(agent, conversation);

            await foreach (var _ in run.RunEventsAsync("seed prompt")) { }

            Assert.Equal(["first", "second", "first", "second"], order);
            Assert.Equal(2, provider.Requests.Count);
            foreach (var request in provider.Requests)
            {
                var transformedPrompt = request.Last(message => message.Role == ChatRole.User).Text;
                Assert.Contains("[first]", transformedPrompt);
                Assert.Contains("[second]", transformedPrompt);
            }
            Assert.Contains(provider.Requests[1].SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>(), result => result.CallId == "ext-call");
            var providerToolCall = Assert.Single(provider.Requests[1].SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>());
            Assert.Equal("provider-only", providerToolCall.Arguments!["value"]);
            Assert.DoesNotContain(run.Conversation.ContextMessages(), message =>
                message.Text.Contains("[first]", StringComparison.Ordinal) ||
                message.Text.Contains("[second]", StringComparison.Ordinal));
            Assert.Contains(run.Conversation.ContextMessages(), message =>
                message.Role == ChatRole.User && message.Text == "seed prompt");
            var canonicalToolCall = Assert.Single(run.Conversation.ContextMessages()
                .SelectMany(message => message.Contents).OfType<FunctionCallContent>());
            Assert.Equal("tool", canonicalToolCall.Arguments!["value"]?.ToString());
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    private static void AppendToLastUserMessage(List<ChatMessage> messages, string suffix)
    {
        var index = messages.FindLastIndex(message => message.Role == ChatRole.User);
        var content = messages[index].Contents.OfType<TextContent>().Last();
        content.Text += suffix;
    }

    private sealed class ContextTransformClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.Select(message => message.Clone()).ToArray());
            if (Requests.Count == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("ext-call", "echo_ext", new Dictionary<string, object?> { ["value"] = "tool" })]);
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
