using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class NativeExtensionHistoricalApiTests
{
    [Fact]
    public async Task Pr8PackageCompiledAgainstTheMergedContractLoadsAndRuns()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var workingDirectory = fixture.ProjectDirectory;
        var agentDirectory = fixture.AgentDirectory;
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(agentDirectory);
        var package = await PackFixtureAsync("NativeExtensionApiV8", Path.Combine(fixture.Root, "packages"));
        AssertPublicPackage(package, "NativeExtensionApiV8Fixture.dll");
        var store = new NativeExtensionPackageStore(agentDirectory);
        var installed = await store.InstallAsync(workingDirectory, package, "user");
        var roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installed]);

        using var catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: roots);

        Assert.Empty(catalog.Warnings);
        Assert.Equal("api-v8 command: still compatible",
            await catalog.Registration.Commands["api-v8-legacy"]("still compatible", CancellationToken.None));
        Assert.Single(catalog.Registration.ContextTransforms);
        Assert.Single(catalog.Registration.ToolCallHooks);
    }

    [Fact]
    public async Task Pr9PackageCompiledAgainstTheMergedContractLoadsAndRuns()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var workingDirectory = fixture.ProjectDirectory;
        var agentDirectory = fixture.AgentDirectory;
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(agentDirectory);
        var package = await PackFixtureAsync("NativeExtensionApiV9", Path.Combine(fixture.Root, "packages"));
        AssertPublicPackage(package, "NativeExtensionApiV9Fixture.dll");
        var store = new NativeExtensionPackageStore(agentDirectory);
        var installed = await store.InstallAsync(workingDirectory, package, "user");
        var roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installed]);

        using var catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: roots);

        Assert.Empty(catalog.Warnings);
        Assert.Equal("api-v9 command: still compatible",
            await catalog.Registration.Commands["api-v9-legacy"]("still compatible", CancellationToken.None));
        Assert.Single(catalog.Registration.ExtensionShortcuts);
        Assert.Single(catalog.Registration.SessionStartHandlers);
    }

    [Fact]
    public async Task Pr11LegacyProviderPackageLoadsAndRunsWithoutFabricatedWireCallbacks()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        Directory.CreateDirectory(fixture.ProjectDirectory);
        Directory.CreateDirectory(fixture.AgentDirectory);
        var package = await PackFixtureAsync("NativeExtensionApiProviderV11", Path.Combine(fixture.Root, "packages"));
        AssertPublicPackage(package, "NativeExtensionApiProviderV11Fixture.dll");
        var store = new NativeExtensionPackageStore(fixture.AgentDirectory);
        var installed = await store.InstallAsync(fixture.ProjectDirectory, package, "user");
        var roots = await store.ResolveAsync(fixture.ProjectDirectory, trusted: true, userSources: [installed]);

        using var catalog = ExtensionCatalog.Load(fixture.AgentDirectory, fixture.ProjectDirectory,
            projectTrusted: true, nativeExtensionPackageRoots: roots);
        Assert.Empty(catalog.Warnings);

        var requestCalls = 0;
        var headerCalls = 0;
        var responseCalls = 0;
        var streamCalls = 0;
        var hooks = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(hooks);
        hooks.SetCurrentSourceInfo(Source("/extensions/current-wire-hooks.dll"));
        adapter.AddBeforeProviderRequestHandler((_, _, _) =>
        {
            requestCalls++;
            return ValueTask.FromResult<JsonElement?>(null);
        });
        adapter.AddBeforeProviderHeadersHandler((_, _, _) =>
        {
            headerCalls++;
            return ValueTask.CompletedTask;
        });
        adapter.AddAfterProviderResponseHandler((_, _, _) =>
        {
            responseCalls++;
            return ValueTask.CompletedTask;
        });
        adapter.AddProviderStreamEventHandler((_, _, _) =>
        {
            streamCalls++;
            return ValueTask.CompletedTask;
        });
        hooks.SetCurrentSourceInfo(null);
        adapter.Close();
        var dispatcher = new NativeExtensionEventDispatcher(hooks, CreateContext(), _ => { });

        using var http = new HttpClient();
        var runtime = await ProviderModelRuntime.CreateAsync(fixture.ProjectDirectory, false, _ => null, http,
            offline: true);
        runtime.SetExtensionProviders(catalog.Registration.ExtensionProviders);
        var client = ProviderChatClientFactory.Create(await runtime.ResolveAsync("legacy_wire_fixture", "legacy-model"));
        using var invocation = ProviderWireInvocation.Enter(dispatcher, CancellationToken.None);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "legacy request")]);

        Assert.Equal("legacy provider response", response.Text);
        Assert.Equal(0, requestCalls);
        Assert.Equal(0, headerCalls);
        Assert.Equal(0, responseCalls);
        Assert.Equal(0, streamCalls);
    }

    [Fact]
    public async Task CurrentCompiledPackageRunsBothContextMutationStages()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        Directory.CreateDirectory(fixture.ProjectDirectory);
        Directory.CreateDirectory(fixture.AgentDirectory);
        var packages = Path.Combine(fixture.Root, "packages");
        var packageV1 = await PackFixtureAsync("NativeExtensionApiV10", packages, "1.0.0", "V1");
        var packageV2 = await PackFixtureAsync("NativeExtensionApiV10", packages, "2.0.0", "V2");
        AssertPublicPackage(packageV1, "NativeExtensionApiV10Fixture.dll");
        AssertPublicPackage(packageV2, "NativeExtensionApiV10Fixture.dll");
        var store = new NativeExtensionPackageStore(fixture.AgentDirectory);
        var installedV1 = await store.InstallAsync(fixture.ProjectDirectory, packageV1, "user");
        var installedV2 = await store.InstallAsync(fixture.ProjectDirectory, packageV2, "user");
        var rootsV1 = await store.ResolveAsync(fixture.ProjectDirectory, trusted: true, userSources: [installedV1]);
        var rootsV2 = await store.ResolveAsync(fixture.ProjectDirectory, trusted: true, userSources: [installedV2]);

        var catalog = ExtensionCatalog.Load(fixture.AgentDirectory, fixture.ProjectDirectory,
            projectTrusted: true, nativeExtensionPackageRoots: rootsV1);
        try
        {
            Assert.Empty(catalog.Warnings);
            Assert.Single(catalog.Registration.ContextHandlers);
            Assert.Single(catalog.Registration.ContextWithSystemHandlers);
            var v1LoadContext = Assert.Single(catalog.NativePackageLoadContextReferences);

            var v1Provider = await RunContextMutationPackageAsync(catalog, fixture.ProjectDirectory, "package v1 prompt");
            var v1Request = Assert.Single(v1Provider.Requests);
            Assert.Contains("package v1 prompt [compiled context handler v1]",
                v1Request.Messages.Last(message => message.Role == ChatRole.User).Text);
            Assert.Equal("compiled context_with_system handler v1", v1Request.Options!.Instructions);

            catalog = ExtensionCatalog.Load(fixture.AgentDirectory, fixture.ProjectDirectory,
                projectTrusted: true, preloaded: catalog, nativeExtensionPackageRoots: rootsV2);
            Assert.True(catalog.Registration.ContextHandlers.Count == 1,
                $"warnings: {string.Join(" | ", catalog.Warnings.Select(warning => warning.Warning))}; " +
                $"errors: {string.Join(" | ", catalog.LoadErrors.Select(error => error.Error))}; root: {rootsV2[0].Path}");
            Assert.Single(catalog.Registration.ContextWithSystemHandlers);
            Assert.Equal(rootsV2[0].Path, Assert.Single(catalog.Registration.ContextHandlers).SourceInfo.Path);
            AssertContextUnloaded(v1LoadContext);

            var v2LoadContext = Assert.Single(catalog.NativePackageLoadContextReferences);
            var v2Provider = await RunContextMutationPackageAsync(catalog, fixture.ProjectDirectory, "package v2 prompt");
            var v2Request = Assert.Single(v2Provider.Requests);
            Assert.Contains("package v2 prompt [compiled context handler v2]",
                v2Request.Messages.Last(message => message.Role == ChatRole.User).Text);
            Assert.Equal("compiled context_with_system handler v2", v2Request.Options!.Instructions);

            catalog = ExtensionCatalog.Load(fixture.AgentDirectory, fixture.ProjectDirectory,
                projectTrusted: true, preloaded: catalog, nativeExtensionPackageRoots: []);
            Assert.Empty(catalog.Registration.ContextHandlers);
            Assert.Empty(catalog.Registration.ContextWithSystemHandlers);
            AssertContextUnloaded(v2LoadContext);
        }
        finally { catalog.Dispose(); }
    }

    private static async Task<string> PackFixtureAsync(string fixtureName, string outputDirectory,
        string packageVersion = "1.0.0", string? contextMutationRevision = null)
    {
        Directory.CreateDirectory(outputDirectory);
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "tests", "fixtures", fixtureName, "Extension",
            fixtureName + ".csproj");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        var arguments = new List<string>
                 {
                     "pack", projectPath, "--configuration", BuildConfiguration(), "--output", outputDirectory,
                     "--nologo", "--disable-build-servers", "-p:NuGetAudit=false",
                     "-p:RestoreIgnoreFailedSources=true", "-p:Version=" + packageVersion
                 };
        if (contextMutationRevision is not null)
            arguments.Add("-p:ContextMutationRevision=" + contextMutationRevision);
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start()) throw new IOException($"Could not start dotnet pack for {fixtureName}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync();
            throw;
        }

        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet pack failed for {fixtureName}:\n{stderr}\n{stdout}");
        var packageName = fixtureName.Replace("NativeExtensionApi", "PiSharp.TestExtensions.NativeApi") +
            $"Fixture.{packageVersion}.nupkg";
        var packagePath = Path.Combine(outputDirectory, packageName);
        return File.Exists(packagePath) ? packagePath :
            throw new FileNotFoundException($"dotnet pack did not create the {fixtureName} package.", packagePath);
    }

    private static void AssertPublicPackage(string packagePath, string assemblyName)
    {
        using var package = ZipFile.OpenRead(packagePath);
        var entries = package.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("pisharp-extension.json", entries);
        Assert.Contains($"lib/net10.0/{assemblyName}", entries);
        Assert.DoesNotContain("lib/net10.0/PiSharp.Extensions.Abstractions.dll", entries);
        Assert.DoesNotContain("lib/net10.0/PiSharp.Runtime.dll", entries);
        Assert.DoesNotContain("lib/net10.0/PiSharp.Cli.dll", entries);
        using var manifest = JsonDocument.Parse(package.GetEntry("pisharp-extension.json")!.Open());
        Assert.Equal(1, manifest.RootElement.GetProperty("apiVersion").GetInt32());
    }

    private static string BuildConfiguration() => new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";

    private static ResourceSourceInfo Source(string path) => new(path, "native-package", "user", "package", null);

    private static PiSharp.Runtime.Extensions.PiSharpExtensionContext CreateContext() => new(
        new PiSharpExtensionContextConfiguration("/tmp", "test", HasUserInterface: false,
            IsProjectTrusted: true, new TestUi()), static () => null, static () => [], static () => true);

    private sealed class TestUi : PiSharp.Runtime.Extensions.IPiSharpExtensionUi
    {
        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> InputAsync(string title, string? placeholder = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public void Notify(string message, string type = "info") { }
    }

    private sealed class ContextMutationPackageClient : IChatClient
    {
        internal List<(ChatMessage[] Messages, ChatOptions? Options)> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add((messages.Select(PiSharpContextTransformPipeline.CloneMessage).ToArray(), options?.Clone()));
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<ContextMutationPackageClient> RunContextMutationPackageAsync(
        ExtensionCatalog catalog, string workingDirectory, string prompt)
    {
        var provider = new ContextMutationPackageClient();
        var agent = new PiAgent(provider, new CodingTools(workingDirectory), noBuiltinTools: true,
            systemPrompt: "host prompt", liveExtensionRegistration: catalog.Registration);
        var run = await ConversationRun.OpenAsync(agent, new ConversationSession(workingDirectory, "fixture", null));
        await foreach (var _ in run.RunEventsAsync(prompt)) { }
        return provider;
    }

    [MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertContextUnloaded(WeakReference context)
    {
        for (var attempt = 0; attempt < 20 && context.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(context.IsAlive, "A replaced native context-mutation package remained reachable.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find PiSharp.slnx.");
    }
}
