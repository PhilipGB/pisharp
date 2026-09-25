using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Tui;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class ExtensionCatalogTests
{
    [Fact]
    public async Task ProjectAssembliesNeedTrustAndUserAssembliesDoNot()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-extension-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(cwd, "agent");
        var project = Path.Combine(cwd, ".pi", "extensions");
        Directory.CreateDirectory(project);
        try
        {
            File.Copy(typeof(FixtureExtension).Assembly.Location, Path.Combine(project, "fixture.dll"));
            using (var untrusted = ExtensionCatalog.Load(agent, cwd, false))
            {
                Assert.Empty(untrusted.Registration.Tools);
                Assert.Empty(untrusted.Registration.UserBashHandlers);
            }
            using (var trusted = ExtensionCatalog.Load(agent, cwd, true))
            {
                Assert.Equal("echo_ext", Assert.Single(trusted.Registration.Tools).Name);
                Assert.NotNull(trusted.Registration.GetToolRenderer("echo_ext"));
                Assert.Single(trusted.Registration.UserBashHandlers);
                Assert.Equal("extension: hello", await trusted.Registration.Commands["fixture"]("hello", CancellationToken.None));
                var client = new ExtensionClient();
                var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd),
                    extensionTools: trusted.Registration.Tools), new ConversationSession(cwd, "fixture", null));
                using var transcriptOutput = new StringWriter();
                using var transcriptStatus = new StringWriter();
                var transcript = new InteractiveTranscript(transcriptOutput, transcriptStatus,
                    toolRenderer: trusted.Registration.GetToolRenderer, workingDirectory: cwd);
                var text = "";
                await foreach (var update in run.RunEventsAsync("invoke plugin"))
                {
                    transcript.Render(update);
                    if (update.Type == "model_text_delta") text += update.Text;
                }
                Assert.Equal("plugin done", text);
                Assert.Equal("extension: tool", client.ToolResult);
                Assert.Contains("extension call: tool", transcriptStatus.ToString());
                Assert.Contains("extension result: tool / extension: tool", transcriptStatus.ToString());
            }
            Directory.CreateDirectory(Path.Combine(agent, "extensions"));
            File.Copy(typeof(FixtureExtension).Assembly.Location, Path.Combine(agent, "extensions", "fixture.dll"));
            Assert.True(CliArguments.Parse(["--no-extensions"]).NoExtensions);
            Assert.True(CliArguments.Parse(["-ne"]).NoExtensions);
            using (var disabled = ExtensionCatalog.Load(agent, cwd, true, discover: false))
            {
                Assert.Empty(disabled.Registration.Tools);
                Assert.Empty(disabled.Registration.Commands);
                Assert.Empty(disabled.Registration.UserBashHandlers);
            }
            var flags = CliArguments.Parse(["-ne", "-e", Path.Combine(project, "fixture.dll")]);
            using (var explicitOnly = ExtensionCatalog.Load(agent, cwd, false, discover: !flags.NoExtensions,
                additionalPaths: flags.ExtensionPaths))
                Assert.Equal("extension: explicit", await explicitOnly.Registration.Commands["fixture"]("explicit", CancellationToken.None));
            using (var explicitDirectory = ExtensionCatalog.Load(agent, cwd, false, discover: false,
                additionalPaths: [project]))
                Assert.Single(explicitDirectory.Registration.Tools);
            using (var deduplicated = ExtensionCatalog.Load(agent, cwd, false,
                additionalPaths: [Path.Combine(agent, "extensions", "fixture.dll")]))
                Assert.Single(deduplicated.Registration.Tools);
            Assert.Throws<FileNotFoundException>(() => ExtensionCatalog.Load(agent, cwd, false, discover: false,
                additionalPaths: ["missing.dll"]));
            Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--extension"]));
            using var personal = ExtensionCatalog.Load(agent, cwd, false);
            Assert.Single(personal.Registration.Tools);
            Assert.Throws<ArgumentException>(() => ExtensionCatalog.Load(agent, cwd, true)); // duplicate names fail closed
        }
        finally { Directory.Delete(cwd, true); }
    }

    [Fact]
    public void RegistrationRejectsReservedNamesAndBuiltInToolsCannotBeShadowed()
    {
        var registration = new ExtensionRegistration();
        Assert.Throws<ArgumentException>(() => registration.AddCommand("quit", (_, _) => Task.FromResult("bad")));
        registration.AddTool(AIFunctionFactory.Create(FixtureExtension.Echo, name: "read"));
        using var client = new ExtensionClient();
        Assert.Throws<ArgumentException>(() => new PiAgent(client, new CodingTools(Path.GetTempPath()),
            extensionTools: registration.Tools));
    }

    [Fact]
    public void RegistrationStoresSafeCallAndResultRenderersByToolName()
    {
        var registration = new ExtensionRegistration();
        var renderer = new PiSharpToolRenderer(
            renderCall: (_, _) => PiSharpToolRenderView.FromText("custom call", PiSharpToolTextStyle.Title),
            renderResult: (_, _) => PiSharpToolRenderView.FromText("custom result", PiSharpToolTextStyle.Success));
        registration.AddTool(AIFunctionFactory.Create(FixtureExtension.Echo, name: "echo_ext"), renderer);

        Assert.Same(renderer, registration.GetToolRenderer("echo_ext"));
        Assert.Same(renderer, Assert.Single(registration.ToolRenderers).Value);
        Assert.Null(registration.GetToolRenderer("unknown"));
        Assert.Throws<ArgumentException>(() => new PiSharpToolRenderer());
    }

    [Fact]
    public async Task ProjectCommandIsRejectedWithoutTrustAndAvailableWithTrustInTerminal()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-extension-pty-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(cwd, ".pi", "extensions");
        Directory.CreateDirectory(folder);
        try
        {
            File.Copy(typeof(FixtureExtension).Assembly.Location, Path.Combine(folder, "fixture.dll"));
            async Task<(string Output, string Error)> Run(bool trusted, bool disableExtensions = false, bool explicitPath = false)
            {
                var cli = typeof(CliArguments).Assembly.Location;
                var start = new ProcessStartInfo("/usr/bin/script")
                {
                    WorkingDirectory = cwd,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "-q", "-e", "-c", $"dotnet '{cli}' --local --no-session --no-tools {(trusted ? "--approve" : "--no-approve")} {(disableExtensions ? "--no-extensions" : "")} {(explicitPath ? "-e '.pi/extensions/fixture.dll'" : "")}", "/dev/null" }
                };
                start.Environment["PISHARP_AGENT_DIR"] = Path.Combine(cwd, "agent");
                using var process = Process.Start(start);
                Assert.NotNull(process);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.StandardInput.WriteAsync("/fixture hi\n/quit\n");
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
                Assert.Equal(0, process.ExitCode);
                return (await stdout, await stderr);
            }
            var denied = await Run(false);
            Assert.Contains("Unknown command: /fixture", denied.Output + denied.Error);
            var allowed = await Run(true);
            Assert.Contains("extension: hi", allowed.Output);
            var disabled = await Run(true, disableExtensions: true);
            Assert.Contains("Unknown command: /fixture", disabled.Output + disabled.Error);
            var explicitlyAllowed = await Run(false, disableExtensions: true, explicitPath: true);
            Assert.Contains("extension: hi", explicitlyAllowed.Output);
        }
        finally { Directory.Delete(cwd, true); }
    }

    private sealed class ExtensionClient : IChatClient
    {
        private int _requests;
        public string? ToolResult { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++_requests == 1)
            {
                Assert.Contains(options?.Tools ?? [], tool => tool.Name == "echo_ext");
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("ext-call", "echo_ext", new Dictionary<string, object?> { ["value"] = "tool" })]);
            }
            else
            {
                ToolResult = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                    .Single(result => result.CallId == "ext-call").Result?.ToString();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "plugin done");
            }
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

/// <summary>Compiled .NET plugin fixture; copied into trust-gated extension directories by tests.</summary>
public sealed class FixtureExtension : IPiSharpExtension
{
    public void Configure(ExtensionRegistration registration)
    {
        registration.AddTool(AIFunctionFactory.Create(Echo, name: "echo_ext"), new PiSharpToolRenderer(
            renderCall: (arguments, _) => PiSharpToolRenderView.FromText(
                "extension call: " + arguments["value"], PiSharpToolTextStyle.Accent),
            renderResult: (result, context) => PiSharpToolRenderView.FromText(
                $"extension result: {context.Arguments["value"]} / {result.Text}", PiSharpToolTextStyle.Success)));
        registration.AddCommand("fixture", (argument, _) => Task.FromResult("extension: " + argument));
        registration.AddUserBashHandler(async (request, _) =>
        {
            if (request.Command != "fixture-bash") return null;
            await request.EmitUpdateAsync("extension: bash update");
            return new BashExecutionResult("extension: bash", "extension: bash", 0, false, false, null);
        });
    }

    [Description("Echo a value through the native extension fixture.")]
    public static string Echo(string value) => "extension: " + value;
}
