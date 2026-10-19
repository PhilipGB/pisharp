using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class InteractiveBashExecutorTests
{
    [Theory]
    [InlineData("!pwd", "pwd", false)]
    [InlineData("  ! printf ok  ", "printf ok", false)]
    [InlineData("!!pwd", "pwd", true)]
    [InlineData(" !! printf ok  ", "printf ok", true)]
    public void ParsesBangCommands(string input, string expectedCommand, bool expectedExcludeFromContext)
    {
        Assert.True(InteractiveBashExecutor.TryParse(input, out var command, out var excludeFromContext));
        Assert.Equal(expectedCommand, command);
        Assert.Equal(expectedExcludeFromContext, excludeFromContext);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("/")]
    [InlineData("!")]
    [InlineData("!!   ")]
    public void LeavesNonCommandsAndEmptyBangInputUnchanged(string input)
    {
        Assert.False(InteractiveBashExecutor.TryParse(input, out _, out _));
    }

    [Fact]
    public async Task CustomUserBashOperationUsesHostPipelineAndRecordsOriginalCommandOnce()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-tui-user-bash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var shellMarker = Path.Combine(workingDirectory, "local-shell-ran");
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            string? observedCommand = null;
            string? observedWorkingDirectory = null;
            var handlerCalls = 0;
            adapter.AddUserBashHandler((eventData, _, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                Assert.Equal($"touch {ProcessTestHelpers.ShellQuote(shellMarker)}", eventData.Command);
                Assert.True(eventData.ExcludeFromContext);
                Assert.Equal(workingDirectory, eventData.WorkingDirectory);
                return ValueTask.FromResult<PiSharpUserBashResult?>(PiSharpUserBashResult.Custom(
                    async (command, cwd, outputSink, _) =>
                    {
                        observedCommand = command;
                        observedWorkingDirectory = cwd;
                        await outputSink(Encoding.UTF8.GetBytes("\u001b[32mremote output\u001b[0m\r\n"));
                        return 17;
                    }));
            });
            adapter.Close();

            var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(),
                new CodingTools(workingDirectory, shellCommandPrefix: "export REMOTE=1"),
                liveExtensionRegistration: registration), new ConversationSession(workingDirectory, "fixture", null));
            var transcriptStatus = new StringWriter();
            var transcript = new InteractiveTranscript(new StringWriter(), transcriptStatus);
            var command = $"touch {ProcessTestHelpers.ShellQuote(shellMarker)}";

            await InteractiveBashExecutor.ExecuteAsync(run, command, excludeFromContext: true, transcript: transcript,
                persist: null, cancellationToken: CancellationToken.None);

            Assert.Equal(1, handlerCalls);
            Assert.Equal($"export REMOTE=1\n{command}", observedCommand);
            Assert.Equal(workingDirectory, observedWorkingDirectory);
            Assert.False(File.Exists(shellMarker));
            var entry = Assert.Single(run.Conversation.Tree.ActivePath(), item => item.Type == "bash_execution");
            Assert.Equal(command, entry.Payload.GetProperty("command").GetString());
            Assert.Equal("remote output\n", entry.Payload.GetProperty("output").GetString());
            Assert.Equal(17, entry.Payload.GetProperty("exitCode").GetInt32());
            Assert.True(entry.Payload.GetProperty("excludeFromContext").GetBoolean());
            Assert.Contains("remote output", transcriptStatus.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workingDirectory)) Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class StubClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
