using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Tests;

public sealed class NativeExtensionBoundaryPackageLifecycleTests
{
    [Fact]
    public async Task PackageReloadRemovalAndCollectionFollowBoundaryHandlerLifecycle()
    {
        using var temporary = new TemporaryDirectory();
        var workingDirectory = Path.Combine(temporary.Path, "project");
        var agentDirectory = Path.Combine(temporary.Path, "agent");
        var packageDirectory = Path.Combine(temporary.Path, "packages");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(packageDirectory);

        var store = new NativeExtensionPackageStore(agentDirectory);
        var v1Archive = await CreatePackageAsync(packageDirectory, "V1", "loader.boundaries", "1.0.0",
            "PiSharp.Test.NativeLoaderExtension.V1.LoaderExtension");
        var v2Archive = await CreatePackageAsync(packageDirectory, "V2", "loader.boundaries", "2.0.0",
            "PiSharp.Test.NativeLoaderExtension.V2.LoaderExtension");
        var installedV1 = await store.InstallAsync(workingDirectory, v1Archive, "user");
        var installedV2 = await store.InstallAsync(workingDirectory, v2Archive, "user");
        var v1Roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV1]);
        var v2Roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV2]);

        var lifecycle = await ExerciseReloadAsync(agentDirectory, workingDirectory, v1Roots, v2Roots);
        AssertContextUnloaded(lifecycle.V1Context);
        var removal = RemovePackage(agentDirectory, workingDirectory, lifecycle.Catalog);
        AssertContextUnloaded(removal.V2Context);
        removal.Catalog.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ReloadResult> ExerciseReloadAsync(string agentDirectory, string workingDirectory,
        IReadOnlyList<NativeExtensionPackageRoot> v1Roots, IReadOnlyList<NativeExtensionPackageRoot> v2Roots)
    {
        var catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: v1Roots);
        Assert.Single(catalog.Registration.TurnEndBoundaryHandlers);
        Assert.Single(catalog.Registration.AgentBeforeSettleHandlers);
        Assert.Single(catalog.Registration.AgentSettledHandlers);
        var v1Context = Assert.Single(catalog.NativePackageLoadContextReferences);
        var v1Observation = await InvokeBoundaryProbesAsync(catalog.Registration);
        Assert.Equal("native-loader-v1-turn", v1Observation.TurnEntry);
        Assert.True(v1Observation.TurnContinue);
        Assert.Equal("native-loader-v1-before-settle", v1Observation.BeforeSettleEntry);
        Assert.True(v1Observation.BeforeSettleContinue);
        Assert.Equal(["native-loader-v1-settled"], v1Observation.SettledEntries);

        catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            preloaded: catalog, nativeExtensionPackageRoots: v2Roots);
        Assert.Single(catalog.Registration.TurnEndBoundaryHandlers);
        Assert.Single(catalog.Registration.AgentBeforeSettleHandlers);
        Assert.Single(catalog.Registration.AgentSettledHandlers);
        var v2Context = Assert.Single(catalog.NativePackageLoadContextReferences);

        var v2Observation = await InvokeBoundaryProbesAsync(catalog.Registration);
        Assert.Equal("native-loader-v2-turn", v2Observation.TurnEntry);
        Assert.False(v2Observation.TurnContinue);
        Assert.Equal("native-loader-v2-before-settle", v2Observation.BeforeSettleEntry);
        Assert.False(v2Observation.BeforeSettleContinue);
        Assert.Equal(["native-loader-v2-settled"], v2Observation.SettledEntries);
        return new(catalog, v1Context, v2Context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RemovalResult RemovePackage(string agentDirectory, string workingDirectory, ExtensionCatalog activeCatalog)
    {
        var v2Context = Assert.Single(activeCatalog.NativePackageLoadContextReferences);
        var removed = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            preloaded: activeCatalog, nativeExtensionPackageRoots: []);
        Assert.Empty(removed.Registration.TurnEndBoundaryHandlers);
        Assert.Empty(removed.Registration.AgentBeforeSettleHandlers);
        Assert.Empty(removed.Registration.AgentSettledHandlers);
        var observation = InvokeBoundaryProbesAsync(removed.Registration).GetAwaiter().GetResult();
        Assert.Null(observation.TurnEntry);
        Assert.False(observation.TurnContinue);
        Assert.Null(observation.BeforeSettleEntry);
        Assert.False(observation.BeforeSettleContinue);
        Assert.Empty(observation.SettledEntries);
        activeCatalog.Dispose();
        return new(removed, v2Context);
    }

    [Fact]
    public async Task ConfigureFailureRollsBackEveryBoundaryHandlerFromThePackage()
    {
        using var temporary = new TemporaryDirectory();
        var workingDirectory = Path.Combine(temporary.Path, "project");
        var agentDirectory = Path.Combine(temporary.Path, "agent");
        var packageDirectory = Path.Combine(temporary.Path, "packages");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(packageDirectory);

        var archive = await CreatePackageAsync(packageDirectory, "V1", "loader.boundary-rollback", "1.0.0",
            "PiSharp.Test.NativeLoaderExtension.V1.ThrowingBoundaryExtension");
        var store = new NativeExtensionPackageStore(agentDirectory);
        var installed = await store.InstallAsync(workingDirectory, archive, "user");
        var roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installed]);
        using var catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: roots);

        Assert.Empty(catalog.Registration.TurnEndBoundaryHandlers);
        Assert.Empty(catalog.Registration.AgentBeforeSettleHandlers);
        Assert.Empty(catalog.Registration.AgentSettledHandlers);
        Assert.Contains(catalog.Warnings, warning => warning.Warning.Contains(
            "fixture boundary configure failure", StringComparison.Ordinal));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<BoundaryProbeResult> InvokeBoundaryProbesAsync(ExtensionRegistration registration)
    {
        var appended = new List<string>();
        var runtimeContext = CreateRuntimeContext(appended);
        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, runtimeContext, diagnostics.Add);
        var turn = new PiSharpExtensionTurnEndEvent(0,
            new ChatMessage(ChatRole.Assistant, [new TextContent("completed")]), [], "assistant-entry", [], aborted: false);
        var turnResult = await dispatcher.DispatchTurnEndBoundaryAsync(turn, PiSharpExtensionAgentOutcome.Completed,
            static _ => new PiSharpBoundaryContextPreview([], [], [], canContinue: false), CancellationToken.None,
            diagnostics.Add);
        var beforeSettleResult = await dispatcher.DispatchAgentBeforeSettleAsync(PiSharpExtensionAgentOutcome.Completed,
            static _ => new PiSharpBoundaryContextPreview([], [], [], canContinue: false), CancellationToken.None,
            diagnostics.Add);
        await dispatcher.DispatchAgentSettledAsync(new(Aborted: false), CancellationToken.None, diagnostics.Add);

        Assert.Empty(diagnostics);
        return new(
            turnResult.Entries.OfType<PiSharpCustomEntryDraft>().SingleOrDefault()?.CustomType,
            turnResult.Continue,
            beforeSettleResult.Entries.OfType<PiSharpCustomEntryDraft>().SingleOrDefault()?.CustomType,
            beforeSettleResult.Continue,
            appended.ToArray());
    }

    private static PiSharp.Runtime.Extensions.PiSharpExtensionContext CreateRuntimeContext(List<string> appended) =>
        CreateRuntimeContextCore(appended);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PiSharp.Runtime.Extensions.PiSharpExtensionContext CreateRuntimeContextCore(List<string> appended)
    {
        var context = new PiSharp.Runtime.Extensions.PiSharpExtensionContext(
            new PiSharpExtensionContextConfiguration("/tmp", "test", HasUserInterface: false,
                IsProjectTrusted: true, new TestUi()), static () => null, static () => [], static () => true);
        context.SetSessionContextFactory(_ => new PiSharpExtensionSessionContext(
            () => new("session", null, null, []),
            (customType, data, _) =>
            {
                appended.Add(customType);
                return Task.FromResult(new NativeExtensions.PiSharpExtensionSessionEntry(
                    "settled-entry", customType, data, DateTimeOffset.UnixEpoch, null));
            }));
        return context;
    }

    private static async Task<string> CreatePackageAsync(string packageDirectory, string fixtureVersion,
        string packageId, string packageVersion, string entryType)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var fixtureDirectory = Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "NativeExtensionLoader",
            "Extension" + fixtureVersion, "bin", configuration, "net10.0");
        var assemblyName = $"PiSharp.Test.NativeLoaderExtension.{fixtureVersion}";
        var packagePath = Path.Combine(packageDirectory, packageId.Replace('.', '-') + "-" + packageVersion + ".nupkg");
        await using var file = File.Create(packagePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        WriteArchiveText(archive, NativeExtensionPackageManifestReader.FileName, JsonSerializer.Serialize(new
        {
            id = packageId,
            version = packageVersion,
            apiVersion = 1,
            extensions = new[] { new { assembly = $"lib/net10.0/{assemblyName}.dll", type = entryType } }
        }));
        foreach (var fileName in new[]
                 {
                     assemblyName + ".dll", assemblyName + ".deps.json",
                     "PiSharp.Test.NativePrivateDependency.dll", "PiSharp.Extensions.Abstractions.dll"
                 })
            AddArchiveFile(archive, $"lib/net10.0/{fileName}", Path.Combine(fixtureDirectory, fileName));
        return packagePath;
    }

    private static void WriteArchiveText(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private static void AddArchiveFile(ZipArchive archive, string name, string path)
    {
        Assert.True(File.Exists(path), $"Native boundary fixture build output is missing: {path}");
        using var target = archive.CreateEntry(name).Open();
        using var source = File.OpenRead(path);
        source.CopyTo(target);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
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
        Assert.False(context.IsAlive, "A replaced boundary callback kept its native package context alive.");
    }

    private sealed record BoundaryProbeResult(string? TurnEntry, bool TurnContinue, string? BeforeSettleEntry,
        bool BeforeSettleContinue, IReadOnlyList<string> SettledEntries);
    private sealed record ReloadResult(ExtensionCatalog Catalog, WeakReference V1Context, WeakReference V2Context);
    private sealed record RemovalResult(ExtensionCatalog Catalog, WeakReference V2Context);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "pisharp-boundary-package-lifecycle-" + Guid.NewGuid().ToString("N"));

        internal string Path { get; }

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

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
}
