using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using Microsoft.Extensions.AI;

namespace PiSharp.Tests;

public sealed class ToolSelectionTests
{
    [Fact]
    public async Task OptInLsMatchesPinnedAsciiListingAndLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-ls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Beta"));
            foreach (var file in new[] { "z.txt", ".env", "alpha.txt" })
                await File.WriteAllTextAsync(Path.Combine(directory, file), "");
            var tool = new DirectoryListingTool(directory);
            Assert.Equal(".env\nalpha.txt\nBeta/\nz.txt", await tool.List());
            Assert.Equal(".env\nalpha.txt\n\n[2 entries limit reached. Use limit=4 for more]", await tool.List(limit: 2));
            Assert.Equal("(empty directory)", await tool.List(limit: 0));
            Assert.Equal($"Not a directory: {Path.Combine(directory, "z.txt")}",
                (await Assert.ThrowsAsync<PiSharp.Runtime.Tools.ToolFailureException>(() => tool.List("z.txt"))).Message);
            var registry = new CodingTools(directory);
            Assert.Equal(["read", "bash", "edit", "write"], registry.Create().Select(t => t.Name));
            Assert.Equal(["ls"], registry.Create(["ls"]).Select(t => t.Name));
            Assert.Empty(registry.Create(noTools: true));
            Assert.Equal(["ls"], registry.Create(["ls"], noTools: true).Select(t => t.Name));
            Assert.Equal(["read"], registry.Create(["read", "bash"], ["bash"]).Select(t => t.Name));
            Assert.Throws<ArgumentException>(() => registry.Create(["not-a-tool"]));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CliAtFileArgumentsAppendUtf8TextAndEscapeFilename()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-at-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "source & notes.txt");
            await File.WriteAllTextAsync(path, "\uFEFFImportant source", new System.Text.UTF8Encoding(true));
            var cli = CliArguments.Parse(["--print", "Explain", "@source & notes.txt"]);
            Assert.Equal(["source & notes.txt"], cli.FileArguments);
            var prompt = await CliFileArguments.AppendTextFilesAsync(cli.Prompt, cli.FileArguments, root);
            Assert.Equal($"<file name=\"{System.Security.SecurityElement.Escape(path)}\">\nImportant source\n</file>\n\nExplain", prompt);
            await Assert.ThrowsAsync<FileNotFoundException>(() => CliFileArguments.AppendTextFilesAsync("", ["missing.txt"], root));
            await File.WriteAllTextAsync(Path.Combine(root, "empty"), "");
            Assert.Equal("", await CliFileArguments.AppendTextFilesAsync("", ["empty"], root));
            await File.WriteAllBytesAsync(Path.Combine(root, "binary"), [0xff, 0x00]);
            await Assert.ThrowsAsync<InvalidDataException>(() => CliFileArguments.AppendTextFilesAsync("", ["binary"], root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CliAtFileArgumentsCreateImageContentAndRejectMismatchedSignatures()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-at-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = Path.Combine(root, "diagram.png");
            await File.WriteAllBytesAsync(image, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
            var prompt = await CliFileArguments.ProcessFilesAsync("Describe it", ["diagram.png"], root);
            Assert.Equal($"<image name=\"{System.Security.SecurityElement.Escape(image)}\" />\n\nDescribe it", prompt.Text);
            Assert.Single(prompt.Images);
            Assert.Equal("image/png", prompt.Images[0].MediaType);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, prompt.Images[0].Data.ToArray());

            await File.WriteAllBytesAsync(image, [0xff, 0x00]);
            await Assert.ThrowsAsync<InvalidDataException>(() => CliFileArguments.ProcessFilesAsync("", ["diagram.png"], root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MafReceivesOnlyTheSelectedToolLoadout()
    {
        var provider = new ToolCaptureClient();
        var tools = new CodingTools(Path.GetTempPath());
        var agent = new PiAgent(provider, tools, ["ls"], noTools: true);
        var session = await agent.CreateSessionAsync();
        await foreach (var _ in agent.RunStreamingAsync("list", session)) { }
        Assert.Equal(["ls"], provider.ToolNames);
        var empty = new PiAgent(provider, tools, noTools: true);
        await foreach (var _ in empty.RunStreamingAsync("no tools", await empty.CreateSessionAsync())) { }
        Assert.Empty(provider.ToolNames);
        var extension = Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "ok", name: "custom");
        var extensionOnly = new PiAgent(provider, tools, extensionTools: [extension], noBuiltinTools: true);
        await foreach (var _ in extensionOnly.RunStreamingAsync("extension only", await extensionOnly.CreateSessionAsync())) { }
        Assert.Equal(["custom"], provider.ToolNames);
        var allowBuiltin = new PiAgent(provider, tools, selectedTools: ["read"], noBuiltinTools: true);
        await foreach (var _ in allowBuiltin.RunStreamingAsync("explicit builtin", await allowBuiltin.CreateSessionAsync())) { }
        Assert.Equal(["read"], provider.ToolNames);
        var excluded = new PiAgent(provider, tools, excludedTools: ["custom"], extensionTools: [extension], noBuiltinTools: true);
        await foreach (var _ in excluded.RunStreamingAsync("excluded extension", await excluded.CreateSessionAsync())) { }
        Assert.Empty(provider.ToolNames);
    }

    [Fact]
    public async Task PiAgentBuildsTheStructuredPromptFromItsActiveBuiltinTools()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var provider = new ToolCaptureClient();
            var agent = new PiAgent(provider, new CodingTools(cwd), ["read", "bash"]);
            await foreach (var _ in agent.RunStreamingAsync("inspect", await agent.CreateSessionAsync())) { }

            Assert.StartsWith("You are an expert coding assistant operating inside pi, a coding agent harness.",
                provider.Instructions, StringComparison.Ordinal);
            Assert.Contains("<tools>\n- read: Read file contents\n- bash: Execute bash commands (ls, grep, find, etc.)",
                provider.Instructions, StringComparison.Ordinal);
            Assert.Contains("<rules>\n- Use bash for file operations like ls, rg, find\n- Use read to examine files instead of cat or sed.",
                provider.Instructions, StringComparison.Ordinal);
            Assert.Contains("<docs>\nPi documentation (read only when the user asks about pi itself", provider.Instructions,
                StringComparison.Ordinal);
            Assert.Contains($"<cwd>\n{cwd.Replace('\\', '/')}\n</cwd>", provider.Instructions, StringComparison.Ordinal);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ToolLoadoutChangesAffectTheNextRequestAndStayScopedToTheirSession()
    {
        var provider = new LoadoutScriptClient();
        var switchTool = AIFunctionFactory.Create((AIFunctionArguments arguments) =>
        {
            PiSharpToolExecutionContext.Get(arguments)!.SetActiveTools(["deferred"]);
            return "activated";
        }, name: "switch_tools");
        var deferredTool = AIFunctionFactory.Create(() => "deferred", name: "deferred");
        var agent = new PiAgent(provider, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            extensionToolRegistrations:
            [
                new(switchTool, ToolExposure.ModelOnly, PrepareLoadout: loadout => new ToolLoadoutChanges(
                    Descriptions: new Dictionary<string, string>
                    {
                        ["switch_tools"] = $"Controls: {string.Join(", ", loadout.Callable.Select(tool => tool.Function.Name))}"
                    }), PromptSnippet: "Switch active tools."),
                new(deferredTool, ToolExposure.Deferred, PromptSnippet: "Run a deferred operation.")
            ]);
        var firstSession = await agent.CreateSessionAsync();
        var secondSession = await agent.CreateSessionAsync();
        var firstLoadout = agent.GetToolLoadout(firstSession);
        var secondLoadout = agent.GetToolLoadout(secondSession);

        Assert.Equal(["switch_tools"], firstLoadout.Snapshot.ActiveToolNames);
        Assert.Equal(["switch_tools"], secondLoadout.Snapshot.ActiveToolNames);
        await foreach (var _ in agent.RunStreamingAsync("switch tools", firstSession)) { }

        Assert.Equal(["switch_tools"], provider.RequestToolNames[0]);
        Assert.Equal(["deferred"], provider.RequestToolNames[1]);
        Assert.Equal("Controls: deferred", provider.RequestToolDescriptions[0]["switch_tools"]);
        Assert.Contains("- switch_tools: Switch active tools.", provider.RequestInstructions[0]);
        Assert.Contains("- deferred: Run a deferred operation.", provider.RequestInstructions[1]);
        Assert.DoesNotContain("- switch_tools:", provider.RequestInstructions[1]);
        Assert.Equal(["deferred"], firstLoadout.Snapshot.ActiveToolNames);
        Assert.Equal(["switch_tools"], secondLoadout.Snapshot.ActiveToolNames);
    }

    [Fact]
    public async Task PiAgentRefreshesMafInvocationToolsAfterExtensionRegistrationChanges()
    {
        var registration = new ExtensionRegistration();
        var source = new PiSharp.Runtime.Resources.ResourceSourceInfo("mcp:docs", "local", "test", "top-level", null);
        registration.ReplaceOwnedTools("mcp:docs",
            [new(Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "old", name: "mcp__docs__old"))], source);
        var provider = new DynamicExtensionClient();
        var agent = new PiAgent(provider, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            extensionToolRegistrations: registration.ToolDefinitions,
            liveExtensionRegistration: registration);
        var session = await agent.CreateSessionAsync();

        await foreach (var _ in agent.RunStreamingAsync("initial", session)) { }

        registration.ReplaceOwnedTools("mcp:docs",
            [new(Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "dynamic result", name: "mcp__docs__new"))],
            source);

        Assert.Equal(["mcp__docs__new"], agent.GetToolLoadout(session).Snapshot.ActiveToolNames);
        await foreach (var _ in agent.RunStreamingAsync("use new tool", session)) { }

        Assert.Equal(["mcp__docs__new"], provider.RequestToolNames[1]);
        Assert.True(provider.SawDynamicToolResult);
    }

    [Fact]
    public async Task PiAgentCanInvokeAMcpToolAddedByANotificationBetweenToolRounds()
    {
        var registration = new ExtensionRegistration();
        var source = new PiSharp.Runtime.Resources.ResourceSourceInfo("mcp:docs", "local", "test", "top-level", null);
        registration.ReplaceOwnedTools("mcp:docs",
            [new(Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "old", name: "mcp__docs__old"))], source);
        var provider = new MidRunRegistrationClient(registration, source);
        var agent = new PiAgent(provider, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            extensionToolRegistrations: registration.ToolDefinitions,
            liveExtensionRegistration: registration);

        await foreach (var _ in agent.RunStreamingAsync("run dynamic tool", await agent.CreateSessionAsync())) { }

        Assert.True(provider.SawDynamicToolResult);
    }

    [Fact]
    public async Task ToolLoadoutChangesSurviveTreeNavigationJsonlExportAndResume()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-tool-loadout-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var switchTool = AIFunctionFactory.Create((AIFunctionArguments arguments) =>
            {
                PiSharpToolExecutionContext.Get(arguments)!.SetActiveTools(["deferred"]);
                return "activated";
            }, name: "switch_tools");
            var deferredTool = AIFunctionFactory.Create(() => "deferred", name: "deferred");
            var registrations = new[]
            {
                new PiSharpToolRegistration(switchTool, ToolExposure.ModelOnly),
                new PiSharpToolRegistration(deferredTool, ToolExposure.Deferred)
            };
            var conversation = new ConversationSession(cwd, "fixture", null);
            var firstClient = new LoadoutScriptClient();
            var firstAgent = new PiAgent(firstClient, new CodingTools(cwd), noBuiltinTools: true,
                extensionToolRegistrations: registrations);
            var firstRun = await ConversationRun.OpenAsync(firstAgent, conversation);
            await foreach (var _ in firstRun.RunEventsAsync("switch tools")) { }

            Assert.Equal(["deferred"], conversation.ActiveToolLoadout());
            var change = Assert.Single(conversation.Tree.Entries, entry => entry.Type == "tool_loadout");
            var changedHead = conversation.Tree.HeadId;
            conversation.Tree.Select(change.ParentId);
            Assert.Null(conversation.ActiveToolLoadout());
            conversation.Tree.Select(changedHead);

            var imported = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(conversation));
            Assert.Equal(["deferred"], imported.ActiveToolLoadout());
            var resumedClient = new ToolCaptureClient();
            var resumedRun = await ConversationRun.OpenAsync(
                new PiAgent(resumedClient, new CodingTools(cwd), noBuiltinTools: true,
                    extensionToolRegistrations: registrations), imported);
            await foreach (var _ in resumedRun.RunEventsAsync("resume")) { }
            Assert.Equal(["deferred"], resumedClient.ToolNames);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ReloadedDefaultToolsMergeNewNamesIntoTheSavedSessionLoadout()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-reload-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var conversation = new ConversationSession(cwd, "fixture", null);
            // bash was disabled during this session. It remains off while the newly added grep is activated.
            conversation.AppendToolLoadout(["read", "write"]);
            var provider = new ToolCaptureClient();
            var previousSettings = new UserSettings(DefaultTools: ["read", "bash", "edit", "write"]);
            var nextSettings = new UserSettings(DefaultTools: ["read", "bash", "+grep"]);
            var plan = DefaultToolReloadPolicy.Resolve(CliArguments.Parse([]), usesSettingsDefaults: true,
                previousSettings, nextSettings, ["read", "write"], _ => null);
            var nextAgent = new PiAgent(provider, new CodingTools(cwd));
            var saveCount = 0;
            var reloaded = await ConversationRun.OpenAsync(nextAgent, conversation,
                save: _ =>
                {
                    saveCount++;
                    return Task.CompletedTask;
                }, activeToolNamesOverride: plan.ActiveToolNames);
            Assert.Equal(1, saveCount);

            await foreach (var _ in reloaded.RunEventsAsync("after reload")) { }

            Assert.Equal(["read", "write", "grep"], provider.ToolNames);
            Assert.Equal(["read", "write", "grep"], conversation.ActiveToolLoadout());
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task HiddenToolCannotRunEvenWhenAProviderReturnsItsName()
    {
        var invoked = false;
        var provider = new HiddenToolCallClient();
        var hidden = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "should not run";
        }, name: "hidden_tool");
        var agent = new PiAgent(provider, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            extensionToolRegistrations: [new(hidden, ToolExposure.Hidden)]);

        await foreach (var _ in agent.RunStreamingAsync("try hidden", await agent.CreateSessionAsync())) { }

        Assert.False(invoked);
        Assert.All(provider.RequestToolNames, names => Assert.Empty(names));
    }

    private sealed class ToolCaptureClient : Microsoft.Extensions.AI.IChatClient
    {
        public IReadOnlyList<string> ToolNames { get; private set; } = [];
        public string Instructions { get; private set; } = string.Empty;
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ToolNames = options?.Tools?.Select(t => t.Name).ToArray() ?? [];
            Instructions = options?.Instructions ?? string.Empty;
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class LoadoutScriptClient : Microsoft.Extensions.AI.IChatClient
    {
        public List<string[]> RequestToolNames { get; } = [];
        public List<Dictionary<string, string>> RequestToolDescriptions { get; } = [];
        public List<string> RequestInstructions { get; } = [];

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestToolNames.Add(options?.Tools?.Select(tool => tool.Name).ToArray() ?? []);
            RequestInstructions.Add(options?.Instructions ?? string.Empty);
            RequestToolDescriptions.Add(options?.Tools?.ToDictionary(tool => tool.Name,
                tool => tool.Description ?? string.Empty, StringComparer.Ordinal) ?? new Dictionary<string, string>());
            if (RequestToolNames.Count == 1)
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant,
                    [new Microsoft.Extensions.AI.FunctionCallContent("switch", "switch_tools", new Dictionary<string, object?>())]);
            else
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class HiddenToolCallClient : Microsoft.Extensions.AI.IChatClient
    {
        private int _requests;
        public List<string[]> RequestToolNames { get; } = [];

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestToolNames.Add(options?.Tools?.Select(tool => tool.Name).ToArray() ?? []);
            if (++_requests == 1)
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant,
                    [new Microsoft.Extensions.AI.FunctionCallContent("hidden", "hidden_tool", new Dictionary<string, object?>())]);
            else
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class DynamicExtensionClient : Microsoft.Extensions.AI.IChatClient
    {
        private int _requests;
        public List<string[]> RequestToolNames { get; } = [];
        public bool SawDynamicToolResult { get; private set; }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = Interlocked.Increment(ref _requests);
            RequestToolNames.Add(options?.Tools?.Select(tool => tool.Name).ToArray() ?? []);
            if (request == 1)
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "ready");
            }
            else if (request == 2)
            {
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant,
                    [new Microsoft.Extensions.AI.FunctionCallContent("dynamic-call", "mcp__docs__new", new Dictionary<string, object?>())]);
            }
            else
            {
                SawDynamicToolResult = messages.SelectMany(message => message.Contents)
                    .OfType<Microsoft.Extensions.AI.FunctionResultContent>().Any(result => result.CallId == "dynamic-call");
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class MidRunRegistrationClient(ExtensionRegistration registration,
        PiSharp.Runtime.Resources.ResourceSourceInfo source) : Microsoft.Extensions.AI.IChatClient
    {
        private int _requests;
        public bool SawDynamicToolResult { get; private set; }

        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, Microsoft.Extensions.AI.ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1)
            {
                registration.ReplaceOwnedTools("mcp:docs",
                    [new(Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "new", name: "mcp__docs__new"))], source);
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant,
                    [new Microsoft.Extensions.AI.FunctionCallContent("mid-run-call", "mcp__docs__new", new Dictionary<string, object?>())]);
            }
            else
            {
                SawDynamicToolResult = messages.SelectMany(message => message.Contents)
                    .OfType<Microsoft.Extensions.AI.FunctionResultContent>().Any(result => result.CallId == "mid-run-call");
                yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void CliToolFiltersAreParsedWithoutConsumingPromptAfterSeparator()
    {
        var args = CliArguments.Parse(["--tools", "read, ls", "--exclude-tools", "ls", "--", "--no-tools"]);
        Assert.Equal(["read", "ls"], args.Tools);
        Assert.Equal(["ls"], args.ExcludeTools);
        Assert.Equal("--no-tools", args.Prompt);
        Assert.True(CliArguments.Parse(["--no-tools"]).NoTools);
        Assert.True(CliArguments.Parse(["--no-builtin-tools"]).NoBuiltinTools);
        Assert.True(CliArguments.Parse(["-nbt", "--tools", "read"]).NoBuiltinTools);
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--tools"]));
    }
}
