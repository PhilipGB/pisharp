using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

[Collection("Codemode workers")]
public sealed class McpCodemodeWorkflowTests
{
    private const string Plan = "Implement a JavaScript identifier validator using parser-spec.md and write src/identifier.js.";

    [Fact]
    public async Task ParentUsesStdioMcpThenCodemodeAndContinuesTheConversation()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-codemode-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var requestLog = Path.Combine(root, "mcp-requests.txt");
        var processIdPath = Path.Combine(root, "mcp-process-id.txt");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "parser-spec.md"),
                "An identifier starts with an ASCII letter or underscore. Later characters may also be digits.");
            var fixture = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Fixtures", "mcp_fixture.py");
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["planner"] = new
                    {
                        command = "python3",
                        args = new[] { fixture },
                        exposure = "direct",
                        env = new Dictionary<string, string>
                        {
                            ["MCP_FIXTURE_REQUEST_LOG"] = requestLog,
                            ["MCP_FIXTURE_PID_PATH"] = processIdPath
                        }
                    }
                }
            }));

            var configuration = await McpConfiguration.LoadAsync(root, root, projectTrusted: false);
            Assert.Empty(configuration.Errors);
            using (var catalog = ExtensionCatalog.Load(root, root, projectTrusted: false, discover: false))
            {
                CodemodeBuiltin.Configure(catalog.Registration);
                Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root));

                var client = new McpThenCodemodeClient(Plan);
                var agent = new PiAgent(client, new CodingTools(root),
                    selectedTools: ["read", "write", "mcp__planner__echo", "codemode"],
                    extensionToolRegistrations: catalog.Registration.ToolDefinitions);
                var conversation = new ConversationSession(root, "fixture", null);
                var run = await ConversationRun.OpenAsync(agent, conversation);
                var events = new List<AgentLifecycleEvent>();

                await foreach (var item in run.RunEventsAsync(
                    "Use the planner, inspect parser-spec.md, and write a validator source file."))
                    events.Add(item);

                Assert.Equal(["codemode", "mcp__planner__echo"], client.InitialToolNames
                    .Where(name => name is "codemode" or "mcp__planner__echo")
                    .Order(StringComparer.Ordinal));
                Assert.Contains(Plan, client.McpResult, StringComparison.Ordinal);
                Assert.Contains("src/identifier.js", client.CodemodeResult, StringComparison.Ordinal);
                Assert.Equal(3, client.RequestCount);

                var generated = await File.ReadAllTextAsync(Path.Combine(root, "src", "identifier.js"));
                Assert.StartsWith("// Plan: " + Plan + "\n", generated, StringComparison.Ordinal);
                Assert.Contains("export function isValidIdentifier(value)", generated, StringComparison.Ordinal);
                Assert.Contains("/^[A-Za-z_][A-Za-z0-9_]*$/", generated, StringComparison.Ordinal);

                var mcpCall = Assert.Single(events, item => item.Type == "tool_execution_finished" &&
                    item.Tool == "mcp__planner__echo");
                Assert.False(mcpCall.IsError);
                var codemodeCall = Assert.Single(events, item => item.Type == "tool_execution_finished" &&
                    item.Tool == "codemode");
                Assert.False(codemodeCall.IsError);
                Assert.Equal(new[] { "read", "write" }, codemodeCall.NestedToolCalls!.Calls
                    .Select(call => call.Name).ToArray());

                await foreach (var item in run.RunEventsAsync("What did the validator accept?")) events.Add(item);

                Assert.Equal(4, client.RequestCount);
                Assert.Contains("Use the planner", client.FollowUpHistory, StringComparison.Ordinal);
                Assert.Contains("Created src/identifier.js", client.FollowUpHistory, StringComparison.Ordinal);
                Assert.Contains("What did the validator accept?", client.FollowUpHistory, StringComparison.Ordinal);
                Assert.Contains(events, item => item.Type == "turn_completed");
            }

            var requests = await File.ReadAllLinesAsync(requestLog);
            Assert.Single(requests, method => method == "tools/call");
            if (OperatingSystem.IsLinux())
                ProcessTestHelpers.WaitForLinuxProcessExit(ProcessTestHelpers.ReadLinuxProcessId(processIdPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ParentCancellationStopsMcpCallAndTheConversationCanContinue()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-cancel-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var requestLog = Path.Combine(root, "mcp-requests.txt");
        var callStarted = Path.Combine(root, "mcp-call-started.txt");
        var processIdPath = Path.Combine(root, "mcp-process-id.txt");
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Fixtures", "mcp_fixture.py");
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["slow"] = new
                    {
                        command = "python3",
                        args = new[] { fixture },
                        exposure = "direct",
                        env = new Dictionary<string, string>
                        {
                            ["MCP_FIXTURE_REQUEST_LOG"] = requestLog,
                            ["MCP_FIXTURE_CALL_STARTED_PATH"] = callStarted,
                            ["MCP_FIXTURE_PID_PATH"] = processIdPath,
                            ["MCP_FIXTURE_BLOCK_CALL"] = "1"
                        }
                    }
                }
            }));
            var configuration = await McpConfiguration.LoadAsync(root, root, projectTrusted: false);
            using (var catalog = ExtensionCatalog.Load(root, root, projectTrusted: false, discover: false))
            {
                Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root));
                var client = new McpCancellationClient();
                var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root),
                    selectedTools: ["mcp__slow__echo"],
                    extensionToolRegistrations: catalog.Registration.ToolDefinitions),
                    new ConversationSession(root, "fixture", null));
                using var cancellation = new CancellationTokenSource();
                var interruptedEvents = new List<AgentLifecycleEvent>();
                var interrupted = Task.Run(async () =>
                {
                    await foreach (var item in run.RunEventsAsync("call the slow MCP tool", cancellation.Token))
                        interruptedEvents.Add(item);
                });

                await ProcessTestHelpers.WaitForFileAsync(callStarted);
                cancellation.Cancel();
                await interrupted.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Contains(interruptedEvents, item => item.Type == "turn_interrupted");
                Assert.Equal("agent_settled", interruptedEvents[^1].Type);
                Assert.DoesNotContain(interruptedEvents, item => item.Type is "turn_completed" or "agent_run_completed");

                var continued = new List<AgentLifecycleEvent>();
                await foreach (var item in run.RunEventsAsync("continue after cancellation")) continued.Add(item);
                Assert.Contains(continued, item => item.Type == "turn_completed");
                Assert.Equal("agent_settled", continued[^1].Type);
                Assert.Equal(2, client.RequestCount);
            }

            var requests = await File.ReadAllLinesAsync(requestLog);
            Assert.Single(requests, method => method == "tools/call");
            if (OperatingSystem.IsLinux())
                ProcessTestHelpers.WaitForLinuxProcessExit(ProcessTestHelpers.ReadLinuxProcessId(processIdPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ParentCancellationStopsCodemodeWorkerAndTheConversationCanContinue()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codemode-cancel-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var toolCalls = 0;
            var hold = AIFunctionFactory.Create(async (CancellationToken cancellationToken) =>
            {
                Interlocked.Increment(ref toolCalls);
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException)
                {
                    canceled.TrySetResult();
                    throw;
                }
                return "unreachable";
            }, name: "hold");
            var registration = new ExtensionRegistration();
            registration.AddTool(new PiSharpToolRegistration(hold, PiSharp.Runtime.Extensions.ToolExposure.CodeMode));
            CodemodeBuiltin.Configure(registration);
            var client = new CodemodeCancellationClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root),
                selectedTools: ["codemode"], noBuiltinTools: true,
                extensionToolRegistrations: registration.ToolDefinitions),
                new ConversationSession(root, "fixture", null));
            using var cancellation = new CancellationTokenSource();
            var interruptedEvents = new List<AgentLifecycleEvent>();
            var interrupted = Task.Run(async () =>
            {
                await foreach (var item in run.RunEventsAsync("start Codemode work", cancellation.Token))
                    interruptedEvents.Add(item);
            });

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await interrupted.WaitAsync(TimeSpan.FromSeconds(5));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (PiSharp.Runtime.Codemode.CodemodeWorkerLifetime.ActiveWorkers > 0)
                    await Task.Delay(10, deadline.Token);
            }

            Assert.Contains(interruptedEvents, item => item.Type == "turn_interrupted");
            Assert.Equal("agent_settled", interruptedEvents[^1].Type);
            Assert.DoesNotContain(interruptedEvents, item => item.Type is "turn_completed" or "agent_run_completed");
            Assert.Equal(1, Volatile.Read(ref toolCalls));

            var continued = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("continue after cancellation")) continued.Add(item);
            Assert.Contains(continued, item => item.Type == "turn_completed");
            Assert.Equal("agent_settled", continued[^1].Type);
            Assert.Equal(2, client.RequestCount);
            Assert.Equal(1, Volatile.Read(ref toolCalls));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class McpThenCodemodeClient(string plan) : IChatClient
    {
        private const string Implementation = "export function isValidIdentifier(value) {\n" +
            "  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(value);\n" +
            "}\n";
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public string[] InitialToolNames { get; private set; } = [];
        public string? McpResult { get; private set; }
        public string? CodemodeResult { get; private set; }
        public string? FollowUpHistory { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The composed acceptance uses the streaming agent path.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = Interlocked.Increment(ref _requestCount);
            var snapshot = messages.ToArray();
            switch (request)
            {
                case 1:
                    InitialToolNames = options?.Tools?.OfType<AIFunction>()
                        .Select(function => function.Name).ToArray() ?? [];
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [
                        new FunctionCallContent("mcp-plan", "mcp__planner__echo",
                            new Dictionary<string, object?> { ["value"] = plan })
                    ]);
                    break;
                case 2:
                    McpResult = ReadResult(snapshot, "mcp-plan");
                    var code = "const plannerResult = " + JsonSerializer.Serialize(McpResult) + "; " +
                        "const spec = await tools.read({path:'parser-spec.md'}); " +
                        "if (!spec.includes('letter') || !spec.includes('digit')) throw new Error('parser spec is incomplete'); " +
                        "const implementation = " + JsonSerializer.Serialize(Implementation) + "; " +
                        "const content = '// Plan: ' + plannerResult + '\\n' + implementation; " +
                        "await tools.write({path:'src/identifier.js',content}); " +
                        "text('Wrote src/identifier.js'); return {path:'src/identifier.js'};";
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [
                        new FunctionCallContent("codemode-task", "codemode",
                            new Dictionary<string, object?> { ["code"] = code })
                    ]);
                    break;
                case 3:
                    CodemodeResult = ReadResult(snapshot, "codemode-task");
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        "Created src/identifier.js from the MCP plan and parser specification.");
                    break;
                case 4:
                    FollowUpHistory = string.Join("\n", snapshot.SelectMany(message => message.Contents)
                        .OfType<TextContent>().Select(content => content.Text));
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        "The validator accepts letters or underscores first, then letters, digits, or underscores.");
                    break;
                default:
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "Unexpected extra model request.");
                    break;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        private static string ReadResult(IReadOnlyList<ChatMessage> messages, string callId)
        {
            var result = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                .Single(content => content.CallId == callId).Result;
            return result switch
            {
                string text => text,
                PiSharpToolResult value => value.Text,
                JsonElement json => json.ToString(),
                _ => JsonSerializer.Serialize(result)
            };
        }
    }

    private sealed class McpCancellationClient : IChatClient
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The cancellation acceptance uses the streaming agent path.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("slow-call", "mcp__slow__echo",
                        new Dictionary<string, object?> { ["value"] = "cancel this request" })
                ]);
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "continued after MCP cancellation");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class CodemodeCancellationClient : IChatClient
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The cancellation acceptance uses the streaming agent path.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent("codemode-call", "codemode",
                        new Dictionary<string, object?> { ["code"] = "text('partial'); await tools.hold({});" })
                ]);
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "continued after Codemode cancellation");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
