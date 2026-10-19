using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
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
            return ValueTask.FromResult<IReadOnlyDictionary<string, string?>?>(null);
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

    private static async Task<string> PackFixtureAsync(string fixtureName, string outputDirectory)
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
        foreach (var argument in new[]
                 {
                     "pack", projectPath, "--configuration", BuildConfiguration(), "--output", outputDirectory,
                     "--nologo", "--disable-build-servers", "-p:NuGetAudit=false",
                     "-p:RestoreIgnoreFailedSources=true"
                 })
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
            "Fixture.1.0.0.nupkg";
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find PiSharp.slnx.");
    }
}
