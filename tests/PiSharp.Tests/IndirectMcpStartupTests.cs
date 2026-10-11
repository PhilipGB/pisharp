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
            var releasePath = Path.Combine(root, "initialize-release");
            var completedPath = Path.Combine(root, "initialize-completed");
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
                            timeout = 15,
                            env = new Dictionary<string, string>
                            {
                                ["MCP_FIXTURE_INITIALIZE_STARTED_PATH"] = startedPath,
                                ["MCP_FIXTURE_INITIALIZE_RELEASE_PATH"] = releasePath,
                                ["MCP_FIXTURE_INITIALIZE_COMPLETED_PATH"] = completedPath,
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
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var context = await ProjectRuntimeContext.LoadAsync(configuration, root, arguments, null,
                cancellationToken: deadline.Token);
            await ProcessTestHelpers.WaitForFileAsync(startedPath, deadline.Token);
            Assert.False(File.Exists(completedPath), "Project context waited for an indirect MCP server to initialize.");
            var model = new ModelDescriptor("fixture", null, 4096, null, Input: ["text"]);
            var provider = new ProviderProfile("fixture", "fixture", new Uri("http://localhost"), false,
                false, null, null, [model]);
            var selection = new ModelSelection(provider, model, "fixture", true, "test");
            var client = new InstructionsClient();
            var agent = context.CreateAgent(client, selection, "off", arguments);
            var session = await agent.CreateSessionAsync(deadline.Token);

            await foreach (var _ in agent.RunStreamingAsync("first prompt", session, deadline.Token)) { }
            var firstInstructions = Assert.Single(client.Instructions);
            Assert.Contains("mcp__slow (codemode", firstInstructions);
            Assert.DoesNotContain("Slow docs.", firstInstructions);

            await File.WriteAllTextAsync(releasePath, "release", deadline.Token);
            while (!context.Extensions.Registration.ToolDefinitions.Any(tool =>
                       tool.Function.Name == "mcp__slow__echo"))
                await Task.Delay(10, deadline.Token);
            Assert.True(File.Exists(completedPath), "The indirect MCP server did not complete initialization after release.");

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
