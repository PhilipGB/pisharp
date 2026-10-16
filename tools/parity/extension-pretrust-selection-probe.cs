using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.ResourceDiscoveryExtension;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

var results = new List<object>();
if (args.Length > 1 && args[1] == "--context")
{
    var root = Path.Combine(args[0], "context");
    var cwd = Path.Combine(root, "project");
    var agent = Path.Combine(root, "agent");
    var trace = Path.Combine(root, "native-context.log");
    Directory.CreateDirectory(cwd);
    Directory.CreateDirectory(agent);
    var previousEnabled = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_CONTEXT");
    var previousTrace = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_CONTEXT_TRACE");
    try
    {
        Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_CONTEXT", "true");
        Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_CONTEXT_TRACE", trace);
        using var catalog = ExtensionCatalog.Load(agent, cwd, false, discover: false,
            additionalPaths: [typeof(ExtensionContextFixture).Assembly.Location]);
        await catalog.DiscoverResourcesAsync(cwd, ExtensionResourceDiscoveryReason.Startup);
        await catalog.DiscoverResourcesAsync(cwd, ExtensionResourceDiscoveryReason.Reload);
        var sourceInfo = catalog.LoadedExtensions.Single();
        var handlers = catalog.Registration.LifecycleHandlers.Select(handler =>
            new ExtensionLifecycleHandlerRegistration(handler, sourceInfo)).ToArray();
        var modelId = "extension-context-model";
        var providerId = "fixture";
        var agentRuntime = new PiAgent(new ContextProbeClient(), new CodingTools(cwd), noBuiltinTools: true,
            extensionToolRegistrations: catalog.Registration.ToolDefinitions,
            extensionLifecycleHandlers: handlers,
            extensionContextConfiguration: new(cwd, "rpc", false, true, new ContextProbeUi()),
            initialActiveToolNames: ["extension_context_probe"]);
        var conversation = new ConversationSession(cwd, modelId, "http://127.0.0.1", providerId);
        var run = await ConversationRun.OpenAsync(agentRuntime, conversation, provider: providerId);
        await foreach (var _ in run.RunEventsAsync("inspect context")) { }
        var lines = await File.ReadAllLinesAsync(trace);
        using var stateDocument = JsonDocument.Parse(lines.Single(line => line.StartsWith("state:", StringComparison.Ordinal))[6..]);
        var rawState = stateDocument.RootElement;
        var state = new
        {
            workingDirectory = rawState.GetProperty("workingDirectory").GetString(),
            mode = rawState.GetProperty("mode").GetString(),
            hasUserInterface = rawState.GetProperty("hasUserInterface").GetBoolean(),
            isProjectTrusted = rawState.GetProperty("isProjectTrusted").GetBoolean(),
            isIdle = rawState.GetProperty("isIdle").GetBoolean(),
            currentModelId = rawState.GetProperty("currentModelId").GetString(),
            currentProviderId = rawState.GetProperty("currentProviderId").GetString(),
            activeToolNames = rawState.GetProperty("activeToolNames").EnumerateArray().Select(item => item.GetString()).ToArray()
        };
        var nativeEvents = lines.Where(line => line.StartsWith("event:", StringComparison.Ordinal))
            .Select(line => line.Split(':')[1] switch
            {
                "tool_execution_started" => "tool_start",
                "tool_execution_update" => "tool_update",
                "tool_execution_finished" => "tool_result",
                var eventType => eventType
            }).ToArray();
        var resourceReasons = lines.Where(line => line.StartsWith("resource:", StringComparison.Ordinal))
            .Select(line => line[9..]).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new { state, events = nativeEvents, resources = resourceReasons }));
    }
    finally
    {
        Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_CONTEXT", previousEnabled);
        Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_CONTEXT_TRACE", previousTrace);
    }
    return;
}

if (args.Length > 1 && args[1] == "--conflicts")
{
    var root = Path.Combine(args[0], "conflicts");
    var cwd = Path.Combine(root, "project");
    var agent = Path.Combine(root, "agent");
    Directory.CreateDirectory(Path.Combine(cwd, ".pi"));
    Directory.CreateDirectory(agent);
    var projectPath = Path.Combine(root, "project.dll");
    var userPath = Path.Combine(root, "user.dll");
    File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, projectPath);
    File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, userPath);
    string Normalize(string value) => value.Replace(projectPath, "<inline:project>", StringComparison.Ordinal)
        .Replace(userPath, "<inline:user>", StringComparison.Ordinal);
    BuiltinExtensionDefinition[] builtins = [new("mcp", registration =>
    {
        registration.AddTool(AIFunctionFactory.Create(() => "builtin", name: "builtin_marker"));
        registration.AddCommand("mcp", (_, _) => Task.FromResult("builtin"), "built-in mcp command");
    }, Replaceable: true)];
    using var catalog = ExtensionCatalog.Load(agent, cwd, false, discover: false,
        additionalPaths: [projectPath, userPath, "builtin:mcp"], builtins: builtins);
    var result = new
    {
        loaded = catalog.LoadedExtensions.Select(extension => Normalize(extension.Path)).ToArray(),
        tool = catalog.Registration.ToolDefinitions.Where(tool => tool.Function.Name == "shared_tool")
            .Select(tool => new { name = tool.Function.Name, description = tool.Function.Description }).SingleOrDefault(),
        tools = catalog.Registration.ToolDefinitions.Select(tool => tool.Function.Name).OrderBy(name => name).ToArray(),
        commands = catalog.Registration.CommandInfo.Select(pair => new { name = pair.Key, description = pair.Value.Description })
            .OrderBy(command => command.name, StringComparer.Ordinal).ToArray(),
        errors = catalog.ConflictDiagnostics.Select(diagnostic => new
        {
            path = Normalize(diagnostic.Path), error = Normalize(diagnostic.Error)
        }).ToArray(),
        warnings = catalog.Warnings.Select(warning => new { path = warning.Path, warning = Normalize(warning.Warning) }).ToArray()
    };
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}

foreach (var id in args.Length > 1 && args[1] == "--ordering" ? new[] { "project-added" } : ["disabled", "enabled", "untrusted"])
{
    var root = Path.Combine(args[0], id);
    var cwd = Path.Combine(root, "project");
    var agent = Path.Combine(root, "agent");
    var package = Path.Combine(root, "package");
    var file = Path.Combine(package, "extensions", "index.dll");
    var trace = Path.Combine(root, "native-trace.log");
    Directory.CreateDirectory(Path.Combine(cwd, ".pi"));
    Directory.CreateDirectory(agent);
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, file);
    File.WriteAllText(Path.Combine(package, "package.json"), "{\"pi\":{\"extensions\":[\"extensions\"]}}");
    if (id == "project-added")
    {
        var projectPackage = Path.Combine(root, "project-package");
        Directory.CreateDirectory(Path.Combine(projectPackage, "extensions"));
        File.Copy(file, Path.Combine(projectPackage, "extensions", "index.dll"));
        File.Copy(Path.Combine(package, "package.json"), Path.Combine(projectPackage, "package.json"));
    }
    var previous = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG");
    var previousDiscovery = Environment.GetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY");
    try
    {
        Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", trace);
        if (id == "project-added") Environment.SetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY", "true");
        using var bootstrap = ExtensionCatalog.Load(agent, cwd, false, userPackages: [new("../package")]);
        var bootstrapCount = bootstrap.LoadedExtensions.Count;
        using var catalog = ExtensionCatalog.Load(agent, cwd, id != "untrusted", preloaded: bootstrap,
            userPackages: [new("../package")], projectPackages: [id == "project-added" ? new("../../project-package") : new("../../package",
                Extensions: id == "enabled" ? null : [], HasFilter: id != "enabled")]);
        string? Relative(string? path) => path is null ? null : Path.GetRelativePath(root, path).Replace(".dll", ".<module>", StringComparison.Ordinal);
        var result = new Dictionary<string, object?> { ["id"] = id, ["bootstrapCount"] = bootstrapCount,
            ["configurationTrace"] = File.ReadAllLines(trace), ["errors"] = catalog.LoadErrors,
            ["loaded"] = catalog.LoadedExtensions.Select(extension => new
            {
                path = Relative(extension.Path),
                sourceInfo = new { path = Relative(extension.Path), source = extension.Source, scope = extension.Scope,
                    origin = extension.Origin, baseDir = Relative(extension.BaseDir) }
            }).ToArray() };
        if (id == "project-added")
        {
            var discovered = await catalog.DiscoverResourcesAsync(cwd, ExtensionResourceDiscoveryReason.Startup);
            result["discoveryBaseDirs"] = discovered.SkillPaths.Select(entry => Relative(entry.SourceInfo.BaseDir)).ToArray();
        }
        results.Add(result);
    }
    finally
    {
        Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", previous);
        Environment.SetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY", previousDiscovery);
    }
}
Console.WriteLine(JsonSerializer.Serialize(results));

sealed class ContextProbeClient : IChatClient
{
    private int _requests;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _requests) == 1)
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("context-probe-call", "extension_context_probe", new Dictionary<string, object?>())]);
        else
            yield return new ChatResponseUpdate(ChatRole.Assistant, "context observed");
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

sealed class ContextProbeUi : IPiSharpExtensionUi
{
    public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
    public Task<string?> InputAsync(string title, string? placeholder = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
    public void Notify(string message, string type = "info") { }
}
