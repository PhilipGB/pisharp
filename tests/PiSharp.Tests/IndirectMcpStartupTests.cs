using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Sessions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class IndirectMcpStartupTests
{
    [Fact]
    public async Task FirstProviderRequestDoesNotWaitForCodemodeServerAndLaterRequestGetsItsInstructions()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-agent-startup-" + Guid.NewGuid().ToString("N"));
        var projectConfig = Path.Combine(root, ".pi");
        Directory.CreateDirectory(projectConfig);
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp_fixture.py");
            var startedPath = Path.Combine(root, "initialize-started");
            await File.WriteAllTextAsync(Path.Combine(projectConfig, "mcp.json"),
                JsonSerializer.Serialize(new
                {
                    mcpServers = new Dictionary<string, object>
                    {
                        ["slow"] = new
                        {
                            command = "python3",
                            args = new[] { fixture },
                            exposure = "codemode",
                            timeout = 5,
                            env = new Dictionary<string, string>
                            {
                                ["MCP_FIXTURE_INITIALIZE_DELAY_MS"] = "2000",
                                ["MCP_FIXTURE_INITIALIZE_STARTED_PATH"] = startedPath,
                                ["MCP_FIXTURE_INSTRUCTIONS"] = "Slow docs.\nAdditional instructions."
                            }
                        }
                    }
                }));

            var arguments = CliArguments.Parse(["--no-extensions", "--extension", "builtin:mcp",
                "--extension", "builtin:codemode"]);
            var trust = new ProjectTrust(root);
            var configuration = await ProjectRuntimeConfiguration.LoadAsync(root, root, arguments, trust,
                interactiveTrust: false, TextReader.Null, TextWriter.Null, trustedOverride: true);
            var loadTime = Stopwatch.StartNew();
            using var context = await ProjectRuntimeContext.LoadAsync(configuration, root, arguments, null);
            Assert.True(loadTime.Elapsed < TimeSpan.FromSeconds(1),
                $"Project context waited {loadTime.ElapsedMilliseconds} ms for an indirect MCP server.");

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await ProcessTestHelpers.WaitForFileAsync(startedPath, deadline.Token);
            var model = new ModelDescriptor("fixture", null, 4096, null, Input: ["text"]);
            var provider = new ProviderProfile("fixture", "fixture", new Uri("http://localhost"), false,
                false, null, null, [model]);
            var selection = new ModelSelection(provider, model, "fixture", true, "test");
            var client = new InstructionsClient();
            var agent = context.CreateAgent(client, selection, "off", arguments);
            var session = await agent.CreateSessionAsync(deadline.Token);

            var firstPrompt = Stopwatch.StartNew();
            await foreach (var _ in agent.RunStreamingAsync("first prompt", session, deadline.Token)) { }
            Assert.True(firstPrompt.Elapsed < TimeSpan.FromSeconds(1),
                $"First provider request waited {firstPrompt.ElapsedMilliseconds} ms for an indirect MCP server.");
            var firstInstructions = Assert.Single(client.Instructions);
            Assert.Contains("mcp__slow (codemode", firstInstructions);
            Assert.DoesNotContain("Slow docs.", firstInstructions);

            while (!context.Extensions.Registration.ToolDefinitions.Any(tool =>
                       tool.Function.Name == "mcp__slow__echo"))
                await Task.Delay(10, deadline.Token);

            await foreach (var _ in agent.RunStreamingAsync("second prompt", session, deadline.Token)) { }
            Assert.Equal(2, client.Instructions.Count);
            var secondInstructions = client.Instructions[1];
            Assert.Contains("mcp__slow (codemode", secondInstructions);
            Assert.Contains("Slow docs.", secondInstructions);
            Assert.DoesNotContain("Additional instructions.", secondInstructions);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class InstructionsClient : IChatClient
    {
        public List<string?> Instructions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Instructions.Add(options?.Instructions);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ready");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
