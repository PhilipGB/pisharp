using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Sessions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class NativeExtensionCatalogTests
{
    [Fact]
    public async Task ProjectNativePackageIsInspectedButNotLoadedBeforeTrust()
    {
        using var temporary = new TemporaryDirectory();
        var workingDirectory = Path.Combine(temporary.Path, "project");
        var agentDirectory = Path.Combine(temporary.Path, "agent");
        var projectSettingsDirectory = Path.Combine(workingDirectory, ".pi");
        Directory.CreateDirectory(projectSettingsDirectory);
        Directory.CreateDirectory(agentDirectory);
        var packageDirectory = Path.Combine(temporary.Path, "packages");
        Directory.CreateDirectory(packageDirectory);
        var package = CreatePackage("1.0.0", "NativeExtensionFixture", "PiSharp.TestExtensions.NativeFixture",
            Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "NativeExtensionFixture", "bin", BuildConfiguration(), "net10.0"),
            packageDirectory);
        var installed = await new NativeExtensionPackageStore(agentDirectory)
            .InstallAsync(workingDirectory, package, "project");
        var source = Path.GetRelativePath(projectSettingsDirectory, installed.Source);
        await File.WriteAllTextAsync(Path.Combine(projectSettingsDirectory, "settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                shellCommandPrefix = 42,
                nativeExtensions = new[] { new { id = installed.Id, version = installed.Version, source } }
            }));

        var arguments = CliArguments.Parse([]);
        var trust = new ProjectTrust(agentDirectory);
        using (var untrustedConfiguration = await ProjectRuntimeConfiguration.LoadAsync(workingDirectory,
                   agentDirectory, arguments, trust, interactiveTrust: false, TextReader.Null, TextWriter.Null,
                   trustedOverride: false))
        {
            Assert.False(untrustedConfiguration.Trusted);
            Assert.Null(untrustedConfiguration.ProjectSettings);
            Assert.Equal("project", Assert.Single(untrustedConfiguration.NativeExtensionPackageRoots).Scope);
            using var untrusted = await ProjectRuntimeContext.LoadAsync(untrustedConfiguration, agentDirectory,
                arguments, null);
            Assert.DoesNotContain("native-fixture", untrusted.Extensions.Registration.Commands.Keys);
            Assert.Contains(untrusted.Extensions.Warnings, warning => warning.Warning.Contains(
                "not loaded because this project is not trusted", StringComparison.Ordinal));
        }

        await File.WriteAllTextAsync(Path.Combine(projectSettingsDirectory, "settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                nativeExtensions = new[] { new { id = installed.Id, version = installed.Version, source } }
            }));
        using var trustedConfiguration = await ProjectRuntimeConfiguration.LoadAsync(workingDirectory,
            agentDirectory, arguments, trust, interactiveTrust: false, TextReader.Null, TextWriter.Null,
            trustedOverride: true);
        using var trusted = await ProjectRuntimeContext.LoadAsync(trustedConfiguration, agentDirectory, arguments, null);
        Assert.True(trusted.Trusted);
        Assert.Contains("native-fixture", trusted.Extensions.Registration.Commands.Keys);
    }

    [Fact]
    public async Task CatalogReloadReconcilesAndUnloadsReplacedNativePackageContext()
    {
        using var temporary = new TemporaryDirectory();
        var workingDirectory = Path.Combine(temporary.Path, "project");
        var agentDirectory = Path.Combine(temporary.Path, "agent");
        var packages = Path.Combine(temporary.Path, "packages");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(packages);

        var fixtureRoot = Path.Combine(FindRepositoryRoot(), "tests", "fixtures");
        var installedV1 = await new NativeExtensionPackageStore(agentDirectory).InstallAsync(workingDirectory,
            CreatePackage("1.0.0", "NativeExtensionFixture", "PiSharp.TestExtensions.NativeFixture",
                Path.Combine(fixtureRoot, "NativeExtensionFixture", "bin", BuildConfiguration(), "net10.0"), packages),
            "user");
        var installedV2 = await new NativeExtensionPackageStore(agentDirectory).InstallAsync(workingDirectory,
            CreatePackage("2.0.0", "NativeExtensionFixtureV2", "PiSharp.TestExtensions.NativeFixture",
                Path.Combine(fixtureRoot, "NativeExtensionFixtureV2", "bin", BuildConfiguration(), "net10.0"), packages),
            "user");

        var store = new NativeExtensionPackageStore(agentDirectory);
        var v1Roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV1]);
        var v2Roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV2]);
        var (catalog, v1LoadContext) = LoadAndReplaceNativePackageCatalog(agentDirectory, workingDirectory,
            v1Roots, v2Roots);
        try
        {
            AssertContextUnloaded(v1LoadContext);
        }
        finally { catalog.Dispose(); }
    }

    [Fact]
    public async Task PackedExtensionsConfigureToolsCommandsAndLifecycleAndReloadByVersion()
    {
        using var temporary = new TemporaryDirectory();
        var workingDirectory = Path.Combine(temporary.Path, "project");
        var agentDirectory = Path.Combine(temporary.Path, "agent");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(agentDirectory);
        var packages = Path.Combine(temporary.Path, "packages");
        Directory.CreateDirectory(packages);

        var versionOne = CreatePackage("1.0.0", "NativeExtensionFixture", "PiSharp.TestExtensions.NativeFixture",
            Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "NativeExtensionFixture", "bin", BuildConfiguration(), "net10.0"),
            packages);
        var versionTwo = CreatePackage("2.0.0", "NativeExtensionFixtureV2", "PiSharp.TestExtensions.NativeFixture",
            Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "NativeExtensionFixtureV2", "bin", BuildConfiguration(), "net10.0"),
            packages);
        var store = new NativeExtensionPackageStore(agentDirectory);
        var installedV1 = await store.InstallAsync(workingDirectory, versionOne, "user");
        var installedV2 = await store.InstallAsync(workingDirectory, versionTwo, "user");

        var bothRoots = await store.ResolveAsync(workingDirectory, trusted: true,
            userSources: [installedV1, installedV2]);
        using (var both = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
                   nativeExtensionPackageRoots: bothRoots))
        {
            Assert.Equal("v1 command / private dependency 1.0.0.0: hello",
                await both.Registration.Commands["native-fixture:1"]("hello", CancellationToken.None));
            Assert.Equal("v2 command / private dependency 2.0.0.0: hello",
                await both.Registration.Commands["native-fixture:2"]("hello", CancellationToken.None));
            var tool = Assert.Single(both.Registration.ToolDefinitions,
                definition => definition.Function.Name == "native_fixture_tool");
            var client = new NativeExtensionToolLoopChatClient();
            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            var agent = new PiAgent(client, new CodingTools(workingDirectory), selectedTools: ["native_fixture_tool"],
                noTools: true, noBuiltinTools: true, extensionToolRegistrations: [tool], systemPrompt: "base system prompt",
                beforeAgentStartHandlers: both.Registration.BeforeAgentStartHandlers);
            var run = await ConversationRun.OpenAsync(agent, conversation);
            await foreach (var _ in run.RunEventsAsync("hello")) { }
            Assert.Equal(2, client.RequestedTools.Count);
            Assert.Contains("native_fixture_tool", client.RequestedTools[0]);
            Assert.Equal("v1 tool / private dependency 1.0.0.0: agent-loop", client.NativeToolResult);
            Assert.Contains("native extension result", client.FinalResponse!, StringComparison.Ordinal);
            Assert.Contains(conversation.ActiveMessages().SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>(), result => result.CallId == "native-tool-call" &&
                    result.Result?.ToString() == "v1 tool / private dependency 1.0.0.0: agent-loop");
            Assert.NotEmpty(client.Instructions);
            var instructions = client.Instructions[0];
            Assert.Contains("v1 extension / private dependency 1.0.0.0", instructions, StringComparison.Ordinal);
            Assert.Contains("v2 extension / private dependency 2.0.0.0", instructions, StringComparison.Ordinal);
            Assert.Contains(workingDirectory, instructions, StringComparison.Ordinal);
        }

        var projectInstall = await store.InstallAsync(workingDirectory, versionOne, "project");
        var untrustedRoots = await store.ResolveAsync(workingDirectory, trusted: false,
            projectSources: [projectInstall]);
        using (var untrusted = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: false,
                   nativeExtensionPackageRoots: untrustedRoots))
        {
            Assert.Empty(untrusted.Registration.Commands);
            Assert.Empty(untrusted.LoadedExtensions);
            Assert.Contains(untrusted.Warnings, warning => warning.Warning.Contains(
                "not loaded because this project is not trusted", StringComparison.Ordinal));
        }
        using (var trusted = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
                   nativeExtensionPackageRoots: untrustedRoots))
            Assert.Contains("native-fixture", trusted.Registration.Commands.Keys);

        var activeV1 = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV1]);
        var reloaded = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: activeV1);
        try
        {
            Assert.StartsWith("v1 command", InvokeNativeFixtureCommand(reloaded, "before"));
            var activeV2 = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV2]);
            reloaded = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
                preloaded: reloaded, nativeExtensionPackageRoots: activeV2);
            var v2Result = InvokeNativeFixtureCommand(reloaded, "after");
            Assert.StartsWith("v2 command", v2Result);
            Assert.Contains("2.0.0.0", v2Result);
        }
        finally { reloaded.Dispose(); }

        var broken = await CreateBrokenEntryPackageAsync(versionOne, Path.Combine(packages, "broken-entry.nupkg"));
        var brokenInstall = await store.InstallAsync(workingDirectory, broken, "user");
        var healthyAndBrokenRoots = await store.ResolveAsync(workingDirectory, trusted: true,
            userSources: [installedV1, brokenInstall]);
        using var isolated = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: healthyAndBrokenRoots);
        Assert.Contains(isolated.Warnings, warning => warning.Warning.Contains(
            "does not contain declared entry type", StringComparison.Ordinal));
        Assert.Contains("native_fixture_tool", isolated.Registration.ToolSourceInfo.Keys);

        var ordinaryClient = new NativeExtensionChatClient();
        var ordinaryAgent = new PiAgent(ordinaryClient, new CodingTools(workingDirectory), systemPrompt: "ordinary prompt",
            extensionToolRegistrations: isolated.Registration.ToolDefinitions,
            beforeAgentStartHandlers: isolated.Registration.BeforeAgentStartHandlers);
        var ordinaryRun = await ConversationRun.OpenAsync(ordinaryAgent,
            new ConversationSession(workingDirectory, "ordinary", null));
        await foreach (var _ in ordinaryRun.RunEventsAsync("ordinary conversation")) { }
        Assert.Contains("ordinary prompt", Assert.Single(ordinaryClient.Instructions), StringComparison.Ordinal);
    }

    private static string CreatePackage(string version, string assemblyName, string packageId,
        string fixtureOutput, string outputDirectory)
    {
        var package = Path.Combine(outputDirectory, $"{packageId}.{version}.nupkg");
        var assemblyFile = assemblyName + ".dll";
        var entryAssemblyPath = "lib/net10.0/" + assemblyFile;
        using var output = File.Create(package);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        var manifest = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = packageId,
            version,
            apiVersion = 1,
            extensions = new[]
            {
                new { assembly = entryAssemblyPath, type = "PiSharp.TestExtensions.NativeFixture.ExampleExtension" }
            }
        });
        WriteArchiveText(archive, "pisharp-extension.json", manifest);
        WriteArchiveText(archive, packageId + ".nuspec",
            $"<?xml version=\"1.0\"?><package><metadata><id>{packageId}</id><version>{version}</version>" +
            "<authors>PiSharp tests</authors><description>Native extension fixture.</description></metadata></package>");
        AddArchiveFile(archive, entryAssemblyPath, Path.Combine(fixtureOutput, assemblyFile));
        AddArchiveFile(archive, "lib/net10.0/" + assemblyName + ".deps.json",
            Path.Combine(fixtureOutput, assemblyName + ".deps.json"));
        AddArchiveFile(archive, "lib/net10.0/NativeExtensionPrivateDependency.dll",
            Path.Combine(fixtureOutput, "NativeExtensionPrivateDependency.dll"));
        return package;
    }

    private static void WriteArchiveText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void AddArchiveFile(ZipArchive archive, string name, string path)
    {
        Assert.True(File.Exists(path), $"Native extension fixture build output is missing: {path}");
        var entry = archive.CreateEntry(name);
        using var target = entry.Open();
        using var source = File.OpenRead(path);
        source.CopyTo(target);
    }

    private static string BuildConfiguration() => new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string InvokeNativeFixtureCommand(ExtensionCatalog catalog, string value) =>
        catalog.Registration.Commands["native-fixture"](value, CancellationToken.None).GetAwaiter().GetResult();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ExtensionCatalog Catalog, WeakReference PreviousLoadContext) LoadAndReplaceNativePackageCatalog(
        string agentDirectory, string workingDirectory, IReadOnlyList<NativeExtensionPackageRoot> v1Roots,
        IReadOnlyList<NativeExtensionPackageRoot> v2Roots)
    {
        var catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: v1Roots);
        var previousLoadContext = Assert.Single(catalog.NativePackageLoadContextReferences);

        var reloaded = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            preloaded: catalog, nativeExtensionPackageRoots: v2Roots);
        Assert.Contains("native-fixture", reloaded.Registration.Commands.Keys);
        Assert.Equal(v2Roots[0].Path, reloaded.Registration.ToolSourceInfo["native_fixture_tool"].Path);
        Assert.DoesNotContain(v1Roots[0].Path,
            reloaded.Registration.CommandInfo.Values.Select(info => info.SourceInfo.Path));
        Assert.DoesNotContain(v1Roots[0].Path,
            reloaded.Registration.BeforeAgentStartHandlers.Select(handler => handler.SourceInfo.Path));
        Assert.DoesNotContain(v1Roots[0].Path, reloaded.LoadedExtensions.Select(extension => extension.Path));
        Assert.Single(reloaded.NativePackageLoadContextReferences);
        return (reloaded, previousLoadContext);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertContextUnloaded(WeakReference context)
    {
        for (var attempt = 0; attempt < 20 && context.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(context.IsAlive, "The native extension load context remained reachable after catalog reload.");
    }

    private static async Task<string> CreateBrokenEntryPackageAsync(string source, string destination)
    {
        await using var input = File.OpenRead(source);
        using var inputArchive = new ZipArchive(input, ZipArchiveMode.Read);
        await using var output = File.Create(destination);
        using var outputArchive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var entry in inputArchive.Entries)
        {
            var target = outputArchive.CreateEntry(entry.FullName);
            await using var targetStream = target.Open();
            if (entry.FullName == "pisharp-extension.json")
            {
                await using var sourceStream = entry.Open();
                using var document = await System.Text.Json.JsonDocument.ParseAsync(sourceStream);
                var manifest = new
                {
                    id = "Example.BrokenEntry",
                    version = "1.0.0",
                    apiVersion = 1,
                    extensions = new[]
                    {
                        new
                        {
                            assembly = document.RootElement.GetProperty("extensions")[0].GetProperty("assembly").GetString(),
                            type = "Example.MissingExtensionType"
                        }
                    }
                };
                await System.Text.Json.JsonSerializer.SerializeAsync(targetStream, manifest);
            }
            else
            {
                await using var sourceStream = entry.Open();
                await sourceStream.CopyToAsync(targetStream);
            }
        }
        return destination;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find PiSharp.slnx.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("pisharp-native-catalog-").FullName;
        internal string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class NativeExtensionToolLoopChatClient : IChatClient
    {
        private int _requests;
        public List<string> Instructions { get; } = [];
        public List<string[]> RequestedTools { get; } = [];
        public string? NativeToolResult { get; private set; }
        public string? FinalResponse { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Instructions.Add(options?.Instructions ?? "");
            RequestedTools.Add(options?.Tools?.OfType<AIFunction>().Select(tool => tool.Name).ToArray() ?? []);
            if (Interlocked.Increment(ref _requests) == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("native-tool-call", "native_fixture_tool",
                        new Dictionary<string, object?> { ["value"] = "agent-loop" })]);
            }
            else
            {
                NativeToolResult = messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>().Single(result => result.CallId == "native-tool-call")
                    .Result?.ToString();
                FinalResponse = "assistant received native extension result";
                yield return new ChatResponseUpdate(ChatRole.Assistant, FinalResponse);
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NativeExtensionChatClient : IChatClient
    {
        public List<string> Instructions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Instructions.Add(options?.Instructions ?? "");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "conversation completed");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
