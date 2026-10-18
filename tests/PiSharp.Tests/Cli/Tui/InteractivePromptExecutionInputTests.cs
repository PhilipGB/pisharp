using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Tui;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class InteractivePromptExecutionInputTests
{
    [Fact]
    public async Task ActiveInputInterceptionTransformsSteeringAndFollowUpAndConsumesHandledInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-tui-active-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var seen = new List<PiSharpInputEvent>();
            var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            adapter.AddInputHandler(async (input, _, cancellationToken) =>
            {
                seen.Add(input);
                if (input.Text == "consume")
                    return PiSharpInputResult.Handled;
                if (input.Text == "cancel")
                {
                    cancellationEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return PiSharpInputResult.Transform(input.Text switch
                {
                    "steer" => "steered",
                    "follow" => "followed",
                    "after-cancel" => "after-cancelled",
                    _ => input.Text
                });
            });
            adapter.Close();
            var resources = await ResourceCatalog.LoadAsync(root, root, trusted: false, discoverSkills: false);
            var client = new BlockingClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root),
                liveExtensionRegistration: registration), new ConversationSession(root, "fixture", null));
            using var runCancellation = new CancellationTokenSource();
            var running = Task.Run(async () =>
            {
                await foreach (var _ in run.RunUserMessageEventsAsync(new Microsoft.Extensions.AI.ChatMessage(
                                   Microsoft.Extensions.AI.ChatRole.User, "active"), "active", runCancellation.Token))
                {
                }
            });
            await client.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var error = new StringWriter();
            using var execution = new InteractivePromptExecution(null, null, () => error, () => resources,
                () => run, () => "", () => CreateSelection(authenticated: true), () => new UserSettings(),
                (_, _, _) => Task.CompletedTask, _ => Task.CompletedTask, null, null);
            Assert.True(await execution.QueueActiveInputAsync("steer", followUp: false, CancellationToken.None));
            Assert.True(await execution.QueueActiveInputAsync("follow", followUp: true, CancellationToken.None));
            Assert.True(await execution.QueueActiveInputAsync("consume", followUp: false, CancellationToken.None));

            using var inputCancellation = new CancellationTokenSource();
            var cancelledInput = execution.QueueActiveInputAsync("cancel", followUp: false, inputCancellation.Token);
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            inputCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledInput);

            var pending = run.GetPendingPrompts();
            Assert.Equal(["steered"], pending.Steering);
            Assert.Equal(["followed"], pending.FollowUp);
            Assert.True(await execution.QueueActiveInputAsync("after-cancel", followUp: false, CancellationToken.None));
            pending = run.GetPendingPrompts();
            Assert.Equal(["steered", "after-cancelled"], pending.Steering);
            Assert.Equal(["followed"], pending.FollowUp);
            Assert.Equal(["steer", "follow", "consume", "cancel", "after-cancel"], seen.Select(input => input.Text));
            Assert.All(seen, input => Assert.Equal(PiSharpInputSource.Interactive, input.Source));
            Assert.Equal([PiSharpInputStreamingBehavior.Steer, PiSharpInputStreamingBehavior.FollowUp,
                PiSharpInputStreamingBehavior.Steer, PiSharpInputStreamingBehavior.Steer,
                PiSharpInputStreamingBehavior.Steer], seen.Select(input => input.StreamingBehavior));
            Assert.Equal(["Queued steering message.", "Queued follow-up.", "Queued steering message."], error.ToString()
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));

            runCancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task HandledIdleInputReturnsBeforeAuthenticationResourceResolutionAndRun()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        adapter.AddInputHandler((_, _, _) => ValueTask.FromResult<PiSharpInputResult?>(PiSharpInputResult.Handled));
        adapter.Close();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new RecordingClient(),
            new CodingTools(Path.GetTempPath()), liveExtensionRegistration: registration), session);
        var selection = CreateSelection(authenticated: false);
        using var execution = new InteractivePromptExecution(null, null, () => TextWriter.Null,
            () => throw new InvalidOperationException("Resource resolution should not run."), () => run, () => "",
            () => selection, () => new UserSettings(), (_, _, _) => Task.CompletedTask,
            _ => Task.CompletedTask, null, null);
        using var cancellation = new CancellationTokenSource();
        var transcript = new InteractiveTranscript(TextWriter.Null, TextWriter.Null, interactive: false);

        await execution.ExecuteAsync("raw handled input", null, selection, new UserSettings(), transcript, cancellation);

        Assert.Empty(session.ActiveMessages());
    }

    [Fact]
    public async Task TransformedInputPreservesOmittedImagesAndClearsExplicitlyEmptyImages()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-tui-input-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            adapter.AddInputHandler((input, _, _) => ValueTask.FromResult<PiSharpInputResult?>(input.Text switch
            {
                "preserve" => PiSharpInputResult.Transform("preserved"),
                "clear" => PiSharpInputResult.Transform("cleared", []),
                _ => PiSharpInputResult.Continue
            }));
            adapter.Close();
            var resources = await ResourceCatalog.LoadAsync(root, root, trusted: false, discoverSkills: false);
            var session = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(new RecordingClient(),
                new CodingTools(root), liveExtensionRegistration: registration), session);
            var selection = CreateSelection(authenticated: true);
            using var execution = new InteractivePromptExecution(null, null, () => TextWriter.Null,
                () => resources, () => run, () => "", () => selection, () => new UserSettings(),
                (_, _, _) => Task.CompletedTask, _ => Task.CompletedTask, null, null);
            var transcript = new InteractiveTranscript(TextWriter.Null, TextWriter.Null, interactive: false);

            foreach (var input in new[] { "preserve", "clear" })
            {
                using var cancellation = new CancellationTokenSource();
                await execution.ExecuteAsync(input, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")],
                    selection, new UserSettings(), transcript, cancellation);
            }

            var userMessages = session.ActiveMessages().Where(message => message.Role == ChatRole.User).ToArray();
            var preserved = Assert.Single(userMessages, message => message.Text == "preserved");
            Assert.Contains(preserved.Contents, content => content is DataContent { MediaType: "image/png" });
            var cleared = Assert.Single(userMessages, message => message.Text == "cleared");
            Assert.DoesNotContain(cleared.Contents, content => content is DataContent);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ModelSelection CreateSelection(bool authenticated)
    {
        var model = new ModelDescriptor("fixture-model", "fixture", 8192, "fixture", Input: ["text", "image"]);
        var provider = new ProviderProfile("fixture", "Fixture", new Uri("http://127.0.0.1"),
            AuthRequired: false, OAuthSupported: false, ApiKeyEnvironment: null, ConfiguredApiKey: null, [model]);
        return new ModelSelection(provider, model, "fixture", authenticated, "fixture");
    }

    private sealed class RecordingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "reply");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class BlockingClient : IChatClient
    {
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
