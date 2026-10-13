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
    public void BuiltinsUseExtensionRegistrationAndRespectExplicitTrustAndDisableRules()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-builtin-extension-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var tool = AIFunctionFactory.Create(() => "ready", name: "fixture_builtin");
            BuiltinExtensionDefinition[] builtins = [new("fixture", registration => registration.AddTool(tool))];
            using (var discovered = ExtensionCatalog.Load(cwd, cwd, false, builtins: builtins))
            {
                Assert.Equal("fixture_builtin", Assert.Single(discovered.Registration.Tools).Name);
                var source = discovered.Registration.ToolSourceInfo["fixture_builtin"];
                Assert.Equal("builtin:fixture", source.Path);
                Assert.Equal("builtin", source.Source);
            }
            using (var disabled = ExtensionCatalog.Load(cwd, cwd, false, discover: false, builtins: builtins))
                Assert.Empty(disabled.Registration.Tools);
            using (var excluded = ExtensionCatalog.Load(cwd, cwd, false, userPaths: ["!builtin:*"], builtins: builtins))
                Assert.Empty(excluded.Registration.Tools);
            using (var included = ExtensionCatalog.Load(cwd, cwd, false,
                userPaths: ["!builtin:*", "+builtin:fixture"], builtins: builtins))
                Assert.Single(included.Registration.Tools);
            using (var projectUntrusted = ExtensionCatalog.Load(cwd, cwd, false,
                projectPaths: ["-builtin:fixture"], builtins: builtins))
                Assert.Single(projectUntrusted.Registration.Tools);
            using (var projectTrusted = ExtensionCatalog.Load(cwd, cwd, true,
                projectPaths: ["-builtin:fixture"], builtins: builtins))
                Assert.Empty(projectTrusted.Registration.Tools);
            using (var explicitLoad = ExtensionCatalog.Load(cwd, cwd, false, discover: false,
                additionalPaths: ["builtin:fixture"], userPaths: ["-builtin:fixture"], builtins: builtins))
                Assert.Single(explicitLoad.Registration.Tools);
            Assert.Throws<FileNotFoundException>(() => ExtensionCatalog.Load(cwd, cwd, false,
                additionalPaths: ["builtin:missing"], builtins: builtins));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ResourceDiscoveryIsOrderedAndAnExtensionFailureDoesNotDiscardOtherPaths()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-resource-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var expectedPath = Path.Combine(cwd, "extension skill");
            BuiltinExtensionDefinition[] builtins =
            [
                new("broken-resources", registration => registration.AddResourceDiscoveryHandler((_, _) =>
                    Task.FromException<ExtensionResourceDiscoveryResult?>(new InvalidOperationException("fixture failure")))),
                new("working-resources", registration => registration.AddResourceDiscoveryHandler((context, _) =>
                    Task.FromResult<ExtensionResourceDiscoveryResult?>(context.Reason == ExtensionResourceDiscoveryReason.Reload
                        ? new([new Uri(expectedPath).AbsoluteUri])
                        : null)))
            ];
            using var catalog = ExtensionCatalog.Load(cwd, cwd, false, discover: false,
                additionalPaths: ["builtin:broken-resources", "builtin:working-resources"], builtins: builtins);

            var discovery = await catalog.DiscoverResourcesAsync(cwd, ExtensionResourceDiscoveryReason.Reload);

            Assert.Equal("fixture failure", Assert.Single(discovery.Errors).Message);
            var skill = Assert.Single(discovery.SkillPaths);
            Assert.Equal(expectedPath, skill.Path);
            Assert.Equal("extension:builtin:working-resources", skill.SourceInfo.Source);
            Assert.Equal("temporary", skill.SourceInfo.Scope);
            Assert.Null(skill.SourceInfo.BaseDir);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

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
                Assert.Empty(untrusted.LoadedExtensions);
            }
            using (var trusted = ExtensionCatalog.Load(agent, cwd, true))
            {
                var loadedExtension = Assert.Single(trusted.LoadedExtensions);
                Assert.Equal(Path.Combine(project, "fixture.dll"), loadedExtension.Path);
                Assert.Equal("project", loadedExtension.Scope);
                Assert.Equal("echo_ext", Assert.Single(trusted.Registration.Tools).Name);
                Assert.NotNull(trusted.Registration.GetToolRenderer("echo_ext"));
                Assert.Single(trusted.Registration.UserBashHandlers);
                Assert.Equal("extension: hello", await trusted.Registration.Commands["fixture"]("hello", CancellationToken.None));
                var projectCommand = trusted.Registration.CommandInfo["fixture"];
                Assert.Equal("Fixture extension command.", projectCommand.Description);
                Assert.Equal(Path.Combine(project, "fixture.dll"), projectCommand.SourceInfo.Path);
                Assert.Equal("auto", projectCommand.SourceInfo.Source);
                Assert.Equal("project", projectCommand.SourceInfo.Scope);
                Assert.Equal(Path.Combine(cwd, ".pi"), projectCommand.SourceInfo.BaseDir);
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
            {
                Assert.Equal("extension: explicit", await explicitOnly.Registration.Commands["fixture"]("explicit", CancellationToken.None));
                var explicitCommand = explicitOnly.Registration.CommandInfo["fixture"];
                Assert.Equal("cli", explicitCommand.SourceInfo.Source);
                Assert.Equal("temporary", explicitCommand.SourceInfo.Scope);
                Assert.Null(explicitCommand.SourceInfo.BaseDir);
            }
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
            var personalCommand = personal.Registration.CommandInfo["fixture"];
            Assert.Equal("auto", personalCommand.SourceInfo.Source);
            Assert.Equal("user", personalCommand.SourceInfo.Scope);
            Assert.Equal(agent, personalCommand.SourceInfo.BaseDir);
            Assert.Throws<ArgumentException>(() => ExtensionCatalog.Load(agent, cwd, true)); // duplicate names fail closed
        }
        finally { Directory.Delete(cwd, true); }
    }

    [Fact]
    public void ConfiguredExtensionPathsAreScopeRelativeFilteredAndTrustGated()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-configured-extension-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(cwd, "agent");
        var assemblyPath = typeof(FixtureExtension).Assembly.Location;
        var userConfigured = Path.Combine(agent, "custom-extensions", "fixture.dll");
        var userAuto = Path.Combine(agent, "extensions", "fixture.dll");
        var projectConfigured = Path.Combine(cwd, ".pi", "custom-extensions", "fixture.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(userConfigured)!);
        Directory.CreateDirectory(Path.GetDirectoryName(userAuto)!);
        Directory.CreateDirectory(Path.GetDirectoryName(projectConfigured)!);
        try
        {
            File.Copy(assemblyPath, userConfigured);
            File.Copy(assemblyPath, userAuto);
            File.Copy(assemblyPath, projectConfigured);

            using (var user = ExtensionCatalog.Load(agent, cwd, projectTrusted: false,
                userPaths: ["custom-extensions", "!extensions/**"]))
            {
                var command = user.Registration.CommandInfo["fixture"];
                Assert.Equal("local", command.SourceInfo.Source);
                Assert.Equal("user", command.SourceInfo.Scope);
                Assert.Equal(Path.GetFullPath(agent), command.SourceInfo.BaseDir);
            }

            using (var untrusted = ExtensionCatalog.Load(agent, cwd, projectTrusted: false, discover: false,
                projectPaths: ["custom-extensions"]))
                Assert.Empty(untrusted.Registration.Commands);

            using var trusted = ExtensionCatalog.Load(agent, cwd, projectTrusted: true, discover: false,
                projectPaths: ["custom-extensions"]);
            Assert.Equal(Path.GetFullPath(projectConfigured), trusted.Registration.CommandInfo["fixture"].SourceInfo.Path);
            Assert.Equal("project", trusted.Registration.CommandInfo["fixture"].SourceInfo.Scope);
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
        registration.AddCommand("fixture", (argument, _) => Task.FromResult("extension: " + argument),
            "Fixture extension command.");
        registration.AddCommand("plain", (_, _) => Task.FromResult("plain"));
        registration.AddCommand("explode", (_, _) =>
            Task.FromException<string>(new InvalidOperationException("fixture command failed")));
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
