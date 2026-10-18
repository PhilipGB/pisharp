using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NativeExtensions = PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class NativeInputAndUserBashDispatchTests
{
    [Fact]
    public async Task InputTransformsChainAcrossHandlersAndPreserveOrClearImagesExplicitly()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = CreateAdapter(registration, cwd, "input.dll");
            var seen = new List<(string Text, string? Name, int Images)>();
            adapter.AddInputHandler((input, _, _) =>
            {
                Assert.Equal(NativeExtensions.PiSharpInputSource.Interactive, input.Source);
                Assert.Null(input.StreamingBehavior);
                seen.Add((input.Text, Assert.Single(input.Images).Name, input.Images.Count));
                input.Images[0].Name = "callback-only";
                return ValueTask.FromResult<NativeExtensions.PiSharpInputResult?>(
                    NativeExtensions.PiSharpInputResult.Transform("two"));
            });
            adapter.AddInputHandler((input, _, _) =>
            {
                seen.Add((input.Text, Assert.Single(input.Images).Name, input.Images.Count));
                return ValueTask.FromResult<NativeExtensions.PiSharpInputResult?>(
                    NativeExtensions.PiSharpInputResult.Transform("three", []));
            });
            adapter.AddInputHandler((input, _, _) =>
            {
                seen.Add((input.Text, input.Images.SingleOrDefault()?.Name, input.Images.Count));
                return ValueTask.FromResult<NativeExtensions.PiSharpInputResult?>(
                    NativeExtensions.PiSharpInputResult.Continue);
            });
            adapter.Close();

            var image = new DataContent(new byte[] { 1, 2, 3 }, "image/png");
            var run = await OpenRunAsync(cwd, registration);
            var result = await run.InterceptInputAsync("one", [image], NativeExtensions.PiSharpInputSource.Interactive,
                null);

            Assert.Equal(NativeExtensions.PiSharpInputResultAction.Transform, result.Action);
            Assert.Equal("three", result.Text);
            Assert.Empty(result.Images!);
            Assert.Equal([("one", null, 1), ("two", null, 1), ("three", null, 0)], seen);
            Assert.Null(image.Name);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task InputHandlerFailureKeepsPriorTransformAndHandledStopsDispatch()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var diagnostics = new List<AgentLifecycleEvent>();
            registration.AddLifecycleHandler((AgentLifecycleEvent item) => diagnostics.Add(item));
            var adapter = CreateAdapter(registration, cwd, "input.dll");
            var laterCalls = 0;
            adapter.AddInputHandler((input, _, _) => ValueTask.FromResult<NativeExtensions.PiSharpInputResult?>(
                NativeExtensions.PiSharpInputResult.Transform(input.Text + "-changed")));
            adapter.AddInputHandler((_, _, _) => throw new InvalidOperationException("handler failed"));
            adapter.AddInputHandler((input, _, _) =>
            {
                laterCalls++;
                Assert.Equal(NativeExtensions.PiSharpInputSource.Rpc, input.Source);
                Assert.Equal(NativeExtensions.PiSharpInputStreamingBehavior.FollowUp, input.StreamingBehavior);
                Assert.Equal("raw-changed", input.Text);
                return ValueTask.FromResult<NativeExtensions.PiSharpInputResult?>(
                    NativeExtensions.PiSharpInputResult.Handled);
            });
            adapter.AddInputHandler((_, _, _) => throw new InvalidOperationException("must be short-circuited"));
            adapter.Close();

            var run = await OpenRunAsync(cwd, registration);
            var result = await run.InterceptInputAsync("raw", null, NativeExtensions.PiSharpInputSource.Rpc,
                NativeExtensions.PiSharpInputStreamingBehavior.FollowUp);

            Assert.Equal(NativeExtensions.PiSharpInputResultAction.Handled, result.Action);
            Assert.Equal(1, laterCalls);
            var diagnostic = Assert.Single(diagnostics, item => item.Type == "extension_error");
            Assert.Contains(Path.Combine(cwd, "input.dll"), System.Text.Json.JsonSerializer.Serialize(diagnostic.Details),
                StringComparison.Ordinal);
            Assert.Contains("input", System.Text.Json.JsonSerializer.Serialize(diagnostic.Details),
                StringComparison.Ordinal);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task InvalidReplacementImagesReportSourceAndKeepPriorInputForLaterHandlers()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var diagnostics = new List<AgentLifecycleEvent>();
            registration.AddLifecycleHandler((AgentLifecycleEvent item) => diagnostics.Add(item));
            var first = CreateAdapter(registration, cwd, "handler-a.dll");
            first.AddInputHandler((input, _, _) => ValueTask.FromResult<NativeExtensions.PiSharpInputResult?>(
                NativeExtensions.PiSharpInputResult.Transform(input.Text + "-after-a")));
            first.Close();

            var second = CreateAdapter(registration, cwd, "handler-b.dll");
            second.AddInputHandler((_, _, _) => ValueTask.FromResult<NativeExtensions.PiSharpInputResult?>(
                NativeExtensions.PiSharpInputResult.Transform("discarded-by-invalid-result",
                    [new DataContent(new byte[] { 9, 8, 7 }, "application/pdf")])));
            second.Close();

            string? laterText = null;
            DataContent? laterImage = null;
            var third = CreateAdapter(registration, cwd, "handler-c.dll");
            third.AddInputHandler((input, _, _) =>
            {
                laterText = input.Text;
                laterImage = Assert.Single(input.Images);
                return ValueTask.FromResult<NativeExtensions.PiSharpInputResult?>(
                    NativeExtensions.PiSharpInputResult.Continue);
            });
            third.Close();

            var client = new RecordingInputClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd),
                    liveExtensionRegistration: registration,
                    extensionLifecycleHandlers: registration.RegisteredLifecycleHandlers),
                new ConversationSession(cwd, "test-model", null));
            var originalImage = new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "prior-image" };
            var result = await run.InterceptInputAsync("raw", [originalImage],
                NativeExtensions.PiSharpInputSource.Interactive, null);

            Assert.Equal(NativeExtensions.PiSharpInputResultAction.Transform, result.Action);
            Assert.Equal("raw-after-a", result.Text);
            Assert.Equal("raw-after-a", laterText);
            Assert.NotNull(laterImage);
            Assert.Equal("prior-image", laterImage.Name);
            Assert.Equal("image/png", laterImage.MediaType);
            Assert.Equal(new byte[] { 1, 2, 3 }, laterImage.Data.ToArray());
            Assert.Single(diagnostics);
            var diagnostic = Assert.Single(diagnostics, item => item.Type == "extension_error");
            var details = System.Text.Json.JsonSerializer.Serialize(diagnostic.Details);
            Assert.Contains(Path.Combine(cwd, "handler-b.dll"), details, StringComparison.Ordinal);
            Assert.Contains("input", details, StringComparison.Ordinal);
            Assert.Contains("application/pdf", diagnostic.Error, StringComparison.Ordinal);
            Assert.Contains("image/*", diagnostic.Error, StringComparison.Ordinal);

            var contents = new List<AIContent> { new TextContent(result.Text!) };
            contents.AddRange(result.Images!);
            await foreach (var _ in run.RunUserMessageEventsAsync(new ChatMessage(ChatRole.User, contents), result.Text!)) { }

            var processed = Assert.Single(client.UserMessages);
            Assert.Equal("raw-after-a", processed.Text);
            Assert.Contains(processed.Contents, content => content is DataContent
            { MediaType: "image/png", Name: "prior-image" });
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task InputCancellationRevokesCallbackContextAndPropagates()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = CreateAdapter(registration, cwd, "cancel.dll");
            NativeExtensions.PiSharpExtensionContext? callbackContext = null;
            NativeExtensions.PiSharpExtensionCapabilities? callbackCapabilities = null;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            adapter.AddInputHandler(async (_, context, cancellationToken) =>
            {
                callbackContext = context;
                callbackCapabilities = context.Capabilities;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return NativeExtensions.PiSharpInputResult.Continue;
            });
            adapter.Close();
            var run = await OpenRunAsync(cwd, registration);
            run.SetExtensionCapabilitiesFactory((_, session) => new NativeExtensions.PiSharpExtensionCapabilities(
                null, session, null, getModel: () => new("fixture", "model", "Fixture", 4096, true)));
            using var cancellation = new CancellationTokenSource();
            var pending = run.InterceptInputAsync("wait", null, NativeExtensions.PiSharpInputSource.Extension,
                null, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.NotNull(callbackContext);
            Assert.NotNull(callbackCapabilities);
            Assert.Throws<ObjectDisposedException>(() => callbackCapabilities.GetModel());
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task CancellationInLaterInputHandlerKeepsPriorTransformAndStopsTheChain()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = CreateAdapter(registration, cwd, "later-cancel.dll");
            var secondHandlerEntered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var laterHandlerCalls = 0;
            adapter.AddInputHandler((input, _, _) => ValueTask.FromResult<NativeExtensions.PiSharpInputResult?>(
                input.Text == "raw" ? NativeExtensions.PiSharpInputResult.Transform("transformed") : null));
            adapter.AddInputHandler(async (input, _, cancellationToken) =>
            {
                secondHandlerEntered.TrySetResult(input.Text);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return NativeExtensions.PiSharpInputResult.Continue;
            });
            adapter.AddInputHandler((_, _, _) =>
            {
                Interlocked.Increment(ref laterHandlerCalls);
                return ValueTask.FromResult<NativeExtensions.PiSharpInputResult?>(null);
            });
            adapter.Close();
            var run = await OpenRunAsync(cwd, registration);
            using var cancellation = new CancellationTokenSource();
            var pending = run.InterceptInputAsync("raw", null, NativeExtensions.PiSharpInputSource.Rpc,
                NativeExtensions.PiSharpInputStreamingBehavior.Steer, cancellation.Token);
            Assert.Equal("transformed", await secondHandlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, laterHandlerCalls);
            Assert.Equal(0, run.PendingPromptCount);
            Assert.Empty(run.GetPendingPrompts().Steering);
            Assert.Empty(run.GetPendingPrompts().FollowUp);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task UserBashUsesFirstNonNullHandlerAcrossNativeAndLegacyRegistrations()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var calls = new List<string>();
            var nativeFirst = CreateAdapter(registration, cwd, "a-native.dll");
            nativeFirst.AddUserBashHandler((request, _, _) =>
            {
                calls.Add("native-null:" + request.Command);
                Assert.True(request.ExcludeFromContext);
                Assert.Equal(cwd, request.WorkingDirectory);
                return ValueTask.FromResult<NativeExtensions.PiSharpUserBashResult?>(null);
            });
            nativeFirst.Close();

            SetSource(registration, cwd, "b-legacy.dll");
            registration.AddUserBashHandler((request, _) =>
            {
                calls.Add("legacy:" + request.Command);
                Assert.True(request.ExcludeFromContext);
                Assert.Equal(cwd, request.WorkingDirectory);
                return Task.FromResult<BashExecutionResult?>(
                    new("captured", "captured", 7, false, true, "/tmp/full.log"));
            });
            registration.SetCurrentSourceInfo(null);

            var nativeLast = CreateAdapter(registration, cwd, "c-native.dll");
            nativeLast.AddUserBashHandler((_, _, _) =>
            {
                calls.Add("must-not-run");
                return ValueTask.FromResult<NativeExtensions.PiSharpUserBashResult?>(null);
            });
            nativeLast.Close();
            registration.OrderExtensions([
                Source(cwd, "a-native.dll"), Source(cwd, "b-legacy.dll"), Source(cwd, "c-native.dll")]);

            var run = await OpenRunAsync(cwd, registration);
            var result = await run.InterceptUserBashAsync("git status", true, _ => Task.CompletedTask);

            Assert.Equal(NativeExtensions.PiSharpUserBashResultKind.FullResult, result!.Kind);
            Assert.Equal("captured", result.Output);
            Assert.Equal(7, result.ExitCode);
            Assert.True(result.Truncated);
            Assert.Equal("/tmp/full.log", result.FullOutputPath);
            Assert.Equal(["native-null:git status", "legacy:git status"], calls);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task UserBashHandlerFailureIsSourceReportedAndDoesNotFallThrough()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = CreateAdapter(registration, cwd, "failed.dll");
            var laterCalls = 0;
            adapter.AddUserBashHandler((_, _, _) => throw new InvalidOperationException("route failed"));
            adapter.AddUserBashHandler((_, _, _) =>
            {
                laterCalls++;
                return ValueTask.FromResult<NativeExtensions.PiSharpUserBashResult?>(null);
            });
            adapter.Close();
            var run = await OpenRunAsync(cwd, registration);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                run.InterceptUserBashAsync("pwd", false, _ => Task.CompletedTask));

            Assert.Contains("user_bash", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, laterCalls);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task UserBashCustomOperationRetainsByteSinkAndNullableStatusContract()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = CreateAdapter(registration, cwd, "custom.dll");
            adapter.AddUserBashHandler((_, _, _) => ValueTask.FromResult<NativeExtensions.PiSharpUserBashResult?>(
                NativeExtensions.PiSharpUserBashResult.Custom(async (command, workingDirectory, outputSink, token) =>
                {
                    Assert.Equal("prefix\\ngit status", command);
                    Assert.Equal(cwd, workingDirectory);
                    token.ThrowIfCancellationRequested();
                    await outputSink(new byte[] { 0x61, 0x62 });
                    return 23;
                })));
            adapter.Close();
            var run = await OpenRunAsync(cwd, registration);
            var result = await run.InterceptUserBashAsync("git status", false, _ => Task.CompletedTask);
            var chunks = new List<byte>();

            Assert.Equal(NativeExtensions.PiSharpUserBashResultKind.CustomOperation, result!.Kind);
            Assert.Null(result.Output);
            Assert.NotNull(result.CustomOperation);
            var exitCode = await result.CustomOperation("prefix\\ngit status", cwd,
                bytes =>
                {
                    chunks.AddRange(bytes.ToArray());
                    return ValueTask.CompletedTask;
                }, CancellationToken.None);

            Assert.Equal(23, exitCode);
            Assert.Equal(new byte[] { 0x61, 0x62 }, chunks);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ModelInvokedBashToolDoesNotDispatchUserBashHandlers()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = CreateAdapter(registration, cwd, "user-bash.dll");
            var userBashCalls = 0;
            adapter.AddUserBashHandler((_, _, _) =>
            {
                Interlocked.Increment(ref userBashCalls);
                return ValueTask.FromResult<NativeExtensions.PiSharpUserBashResult?>(null);
            });
            adapter.Close();

            var run = await ConversationRun.OpenAsync(new PiAgent(new ModelBashToolClient(),
                new CodingTools(cwd), selectedTools: ["bash"], liveExtensionRegistration: registration),
                new ConversationSession(cwd, "test-model", null));
            await foreach (var _ in run.RunUserMessageEventsAsync(new ChatMessage(ChatRole.User, "run the tool"),
                               "run the tool")) { }

            Assert.Equal(0, userBashCalls);
            Assert.Contains(run.Conversation.ActiveMessages().SelectMany(message => message.Contents), content =>
                content is FunctionResultContent { CallId: "model-bash-call" });
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public void InputAndUserBashRegistrationsRollbackAndReconcileBySource()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var firstSource = Source(cwd, "first.dll");
            var first = CreateAdapter(registration, cwd, "first.dll");
            first.AddInputHandler((_, _, _) => ValueTask.FromResult<NativeExtensions.PiSharpInputResult?>(null));
            first.AddUserBashHandler((_, _, _) => ValueTask.FromResult<NativeExtensions.PiSharpUserBashResult?>(null));
            first.Close();
            registration.SetCurrentSourceInfo(null);

            var rollback = registration.CreateConfigurationCheckpoint();
            var second = CreateAdapter(registration, cwd, "second.dll");
            second.AddInputHandler((_, _, _) => ValueTask.FromResult<NativeExtensions.PiSharpInputResult?>(null));
            second.AddUserBashHandler((_, _, _) => ValueTask.FromResult<NativeExtensions.PiSharpUserBashResult?>(null));
            second.Close();
            rollback();

            Assert.Single(registration.InputHandlers);
            Assert.Single(registration.RegisteredUserBashHandlers);
            registration.ReconcileExtension(firstSource, selected: null);
            Assert.Empty(registration.InputHandlers);
            Assert.Empty(registration.RegisteredUserBashHandlers);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    private static NativeExtensionRegistrationAdapter CreateAdapter(ExtensionRegistration registration,
        string cwd, string fileName)
    {
        SetSource(registration, cwd, fileName);
        return new NativeExtensionRegistrationAdapter(registration);
    }

    private static void SetSource(ExtensionRegistration registration, string cwd, string fileName) =>
        registration.SetCurrentSourceInfo(Source(cwd, fileName));

    private static ResourceSourceInfo Source(string cwd, string fileName)
    {
        var path = Path.Combine(cwd, fileName);
        return new(path, "local", "user", "extension", cwd);
    }

    private static async Task<ConversationRun> OpenRunAsync(string cwd, ExtensionRegistration registration) =>
        await ConversationRun.OpenAsync(new PiAgent(new NoInferenceClient(), new CodingTools(cwd),
                liveExtensionRegistration: registration,
                extensionLifecycleHandlers: registration.RegisteredLifecycleHandlers),
            new ConversationSession(cwd, "test-model", null));

    private static string NewWorkingDirectory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-input-userbash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        return cwd;
    }

    private sealed class NoInferenceClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new NotSupportedException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingInputClient : IChatClient
    {
        public List<ChatMessage> UserMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            UserMessages.AddRange(messages.Where(message => message.Role == ChatRole.User));
            yield return new ChatResponseUpdate(ChatRole.Assistant, "processed");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ModelBashToolClient : IChatClient
    {
        private int _requests;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                Assert.Contains(options?.Tools ?? [], tool => tool is AIFunction function && function.Name == "bash");
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("model-bash-call", "bash",
                        new Dictionary<string, object?> { ["command"] = "printf model-invoked-bash" })]);
            }
            else
            {
                Assert.Contains(messages.SelectMany(message => message.Contents), content =>
                    content is FunctionResultContent { CallId: "model-bash-call" });
                yield return new ChatResponseUpdate(ChatRole.Assistant, "finished");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
