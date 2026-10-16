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
}
