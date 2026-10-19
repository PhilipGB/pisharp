using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

[Collection("Process timing")]
public sealed class NativeExtensionExecPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pisharp-exec-package-" + Guid.NewGuid().ToString("N"));
    private string Agent => Path.Combine(_root, "agent");
    private string Project => Path.Combine(_root, "project");

    public NativeExtensionExecPackageTests()
    {
        Directory.CreateDirectory(Agent);
        Directory.CreateDirectory(Project);
    }

    [Fact]
    public async Task CompiledCommandToolAndAsyncEventUseExecutorCapturedDuringConfigure()
    {
        var roots = await InstallAsync("1.0.0");
        using var catalog = ExtensionCatalog.Load(Agent, Project, true, nativeExtensionPackageRoots: roots);
        Assert.Empty(catalog.Warnings);
        AssertResult(await catalog.Registration.Commands["native-exec"]("command-output", CancellationToken.None), "command-output");
        var tool = Assert.Single(catalog.Registration.Tools);
        AssertResult((await tool.InvokeAsync(new AIFunctionArguments { ["text"] = "tool-output" }))!.ToString()!, "tool-output");
        var log = Path.Combine(_root, "event.log");
        var previous = Environment.GetEnvironmentVariable("PISHARP_NATIVE_EXEC_EVENT_LOG");
        Environment.SetEnvironmentVariable("PISHARP_NATIVE_EXEC_EVENT_LOG", log);
        try
        {
            var handler = Assert.Single(catalog.Registration.SessionStartHandlers);
            await handler.Handler(PiSharpExtensionSessionStartReason.Startup, CreateContext(), CancellationToken.None);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(log));
            Assert.Equal("event-output", result.RootElement.GetProperty("Stdout").GetString());
            Assert.Equal("event-error", result.RootElement.GetProperty("Stderr").GetString());
        }
        finally { Environment.SetEnvironmentVariable("PISHARP_NATIVE_EXEC_EVENT_LOG", previous); }
        // The event's public context has now been disposed. The package handle remains valid.
        AssertResult(await catalog.Registration.Commands["native-exec"]("after-event", CancellationToken.None), "after-event");
    }

    [Fact]
    public async Task ReplacementAndRemovalRevokeHandlesCancelProcessesAndLeaveAlcsCollectible()
    {
        var firstRoots = await InstallAsync("1.0.0");
        var secondRoots = await InstallAsync("2.0.0");
        var state = await ReplaceAsync(firstRoots, secondRoots);
        AssertCollected(state.PreviousContext);
        Assert.Throws<ObjectDisposedException>(() => { _ = state.PreviousExecutor.ExecuteAsync("dotnet", []); });
        var nextContext = Assert.Single(state.Catalog.NativePackageLoadContextReferences);
        var nextExecutor = GetExecutor(nextContext);
        var marker = Path.Combine(_root, "removed");
        var pending = nextExecutor.ExecuteAsync("dotnet", [NativeExtensionProcessExecutionTests.FixtureAssembly, "tree", marker]);
        await ProcessTestHelpers.WaitForFileAsync(marker);
        var removed = ExtensionCatalog.Load(Agent, Project, true, preloaded: state.Catalog, nativeExtensionPackageRoots: []);
        Assert.True((await pending).Killed);
        Assert.Throws<ObjectDisposedException>(() => { _ = nextExecutor.ExecuteAsync("dotnet", []); });
        Assert.Empty(removed.Registration.Commands);
        Assert.Empty(removed.Registration.Tools);
        Assert.Empty(removed.Registration.SessionStartHandlers);
        AssertCollected(nextContext);
        removed.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<Replacement> ReplaceAsync(IReadOnlyList<NativeExtensionPackageRoot> firstRoots,
        IReadOnlyList<NativeExtensionPackageRoot> secondRoots)
    {
        var catalog = ExtensionCatalog.Load(Agent, Project, true, nativeExtensionPackageRoots: firstRoots);
        var previousContext = Assert.Single(catalog.NativePackageLoadContextReferences);
        var executor = GetExecutor(previousContext);
        var marker = Path.Combine(_root, "replaced");
        var running = executor.ExecuteAsync("dotnet", [NativeExtensionProcessExecutionTests.FixtureAssembly, "tree", marker]);
        await ProcessTestHelpers.WaitForFileAsync(marker);
        catalog = ExtensionCatalog.Load(Agent, Project, true, preloaded: catalog, nativeExtensionPackageRoots: secondRoots);
        Assert.True((await running).Killed);
        if (OperatingSystem.IsLinux())
        {
            ProcessTestHelpers.WaitForLinuxProcessExit(ProcessTestHelpers.ReadLinuxProcessId(marker));
            ProcessTestHelpers.WaitForLinuxProcessExit(ProcessTestHelpers.ReadLinuxProcessId(marker + ".child"));
        }
        AssertResult(await catalog.Registration.Commands["native-exec"]("replacement", CancellationToken.None), "replacement");
        return new(catalog, previousContext, executor);
    }

    [Fact]
    public async Task ConfigureFailureCancelsItsStartedProcessAndRollsBackRegistrations()
    {
        var roots = await InstallAsync("1.0.0");
        var marker = Path.Combine(_root, "failed-configure");
        var previous = Environment.GetEnvironmentVariable("PISHARP_NATIVE_EXEC_FAIL_CONFIGURE");
        Environment.SetEnvironmentVariable("PISHARP_NATIVE_EXEC_FAIL_CONFIGURE", marker);
        try
        {
            using var catalog = ExtensionCatalog.Load(Agent, Project, true, nativeExtensionPackageRoots: roots);
            Assert.Empty(catalog.Registration.Commands);
            Assert.Empty(catalog.Registration.Tools);
            Assert.Empty(catalog.Registration.SessionStartHandlers);
            Assert.Contains(catalog.Warnings, warning => warning.Warning.Contains("exec fixture Configure failure", StringComparison.Ordinal));
            Assert.True(File.Exists(marker));
            if (OperatingSystem.IsLinux()) ProcessTestHelpers.WaitForLinuxProcessExit(ProcessTestHelpers.ReadLinuxProcessId(marker));
        }
        finally { Environment.SetEnvironmentVariable("PISHARP_NATIVE_EXEC_FAIL_CONFIGURE", previous); }
    }

    [Fact]
    public async Task CompiledCommandPassesItsOperationCancellationToTheRetainedExecutor()
    {
        var roots = await InstallAsync("1.0.0");
        using var catalog = ExtensionCatalog.Load(Agent, Project, true, nativeExtensionPackageRoots: roots);
        using var cancelled = new CancellationTokenSource();
        var marker = Path.Combine(_root, "cancelled-command");
        var pending = catalog.Registration.Commands["native-exec"]("wait:" + marker, cancelled.Token);
        await ProcessTestHelpers.WaitForFileAsync(marker);
        cancelled.Cancel();
        var result = JsonSerializer.Deserialize<PiSharpExtensionExecResult>(await pending)!;
        Assert.True(result.Killed);
        Assert.Equal(0, result.ExitCode);
        if (OperatingSystem.IsLinux())
        {
            ProcessTestHelpers.WaitForLinuxProcessExit(ProcessTestHelpers.ReadLinuxProcessId(marker));
            ProcessTestHelpers.WaitForLinuxProcessExit(ProcessTestHelpers.ReadLinuxProcessId(marker + ".child"));
        }
        AssertResult(await catalog.Registration.Commands["native-exec"]("after-cancel", CancellationToken.None), "after-cancel");
    }

    [Fact]
    public async Task UntrustedProjectPackageCannotObtainAnExecutorOrRunConfigure()
    {
        var package = await CreatePackageAsync(_root, "1.0.0");
        var store = new NativeExtensionPackageStore(Agent);
        var installed = await store.InstallAsync(Project, package, "project");
        var roots = await store.ResolveAsync(Project, trusted: false, projectSources: [installed]);
        using var catalog = ExtensionCatalog.Load(Agent, Project, false, nativeExtensionPackageRoots: roots);
        Assert.Empty(catalog.NativePackageLoadContextReferences);
        Assert.Empty(catalog.Registration.Commands);
    }

    private async Task<IReadOnlyList<NativeExtensionPackageRoot>> InstallAsync(string version)
    {
        var store = new NativeExtensionPackageStore(Agent);
        var archive = await CreatePackageAsync(_root, version);
        var installed = await store.InstallAsync(Project, archive, "user");
        return await store.ResolveAsync(Project, trusted: true, userSources: [installed]);
    }

    [Fact]
    public async Task UnloadDeadlineReleasesPackageWhileOwnedExecutionContinuesStopping()
    {
        if (!OperatingSystem.IsLinux() || ProcessLifetime.FindExecutable("setsid") is null) return;
        var root = Assert.Single(await InstallAsync("1.0.0"));
        using var handle = ConfigureWithImmediateUnloadDeadline(root.Path);
        var context = handle.LoadContextReference;
        var executor = GetExecutor(context);
        var marker = Path.Combine(_root, "unload-deadline");
        var pending = executor.ExecuteAsync("dotnet",
            [NativeExtensionProcessExecutionTests.FixtureAssembly, "wait", marker, "ignore-term"]);
        try
        {
            await ProcessTestHelpers.WaitForFileAsync(marker);
            // The child ignores TERM and remains owned during the five-second escalation grace.
            // An immediate unload deadline exercises the pending-task path without a 15-second delay.
            handle.Unload();
            Assert.False(pending.IsCompleted);
            Assert.Null(handle.ProcessExecutor);
            Assert.Null(handle.LoadContext);
            Assert.Throws<ObjectDisposedException>(() => { _ = executor.ExecuteAsync("dotnet", []); });
            AssertCollected(context);
        }
        finally
        {
            handle.Unload();
            Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(15))).Killed);
            ProcessTestHelpers.WaitForLinuxProcessExit(ProcessTestHelpers.ReadLinuxProcessId(marker));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private NativeExtensionPackageHandle ConfigureWithImmediateUnloadDeadline(string path)
    {
        var loaded = NativeExtensionPackageLoader.Load(path, new(path, "fixture", "user", "package", path));
        Assert.Null(loaded.Error);
        var handle = Assert.IsType<NativeExtensionPackageHandle>(loaded.Handle);
        handle.InitializeProcessExecutor(Project, TimeSpan.Zero);
        var builder = new NativeExtensionRegistrationAdapter(new(), handle.ProcessExecutor);
        try
        {
            foreach (var extension in handle.Extensions) extension.Configure(builder);
        }
        finally { builder.Close(); }
        handle.ReleaseExtensions();
        return handle;
    }

    internal static async Task<string> CreatePackageAsync(string root, string version)
    {
        var path = Path.Combine(root, "native-exec-" + version + ".nupkg");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("NativeExec.nuspec").Open()))
            await writer.WriteAsync($"<package><metadata><id>PiSharp.TestExtensions.NativeExec</id><version>{version}</version><authors>PiSharp</authors><description>Exec fixture</description></metadata></package>");
        using (var writer = new StreamWriter(archive.CreateEntry("pisharp-extension.json").Open()))
            await writer.WriteAsync(JsonSerializer.Serialize(new
            {
                id = "PiSharp.TestExtensions.NativeExec",
                version,
                apiVersion = 1,
                extensions = new[] { new { assembly = "lib/net10.0/NativeExecExtension.dll", type = "PiSharp.TestExtensions.NativeExec.ExecExtension" } }
            }));
        foreach (var suffix in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            archive.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, "NativeExecFixture", "NativeExecExtension" + suffix),
                "lib/net10.0/NativeExecExtension" + suffix);
        return path;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PiSharpExtensionProcessExecutor GetExecutor(WeakReference reference)
    {
        var context = (AssemblyLoadContext)reference.Target!;
        var assembly = Assert.Single(context.Assemblies, value => value.GetName().Name == "NativeExecExtension");
        return (PiSharpExtensionProcessExecutor)assembly.GetType("PiSharp.TestExtensions.NativeExec.ExecExtension")!
            .GetProperty("CapturedExecutor")!.GetValue(null)!;
    }

    private static void AssertCollected(WeakReference context)
    {
        for (var index = 0; index < 20 && context.IsAlive; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(context.IsAlive, "A retained host executor kept package code alive.");
    }

    private static void AssertResult(string value, string stdout)
    {
        var result = JsonSerializer.Deserialize<PiSharpExtensionExecResult>(value)!;
        Assert.Equal(new(stdout, "fixture-error", 7, false), result);
    }

    private PiSharp.Runtime.Extensions.PiSharpExtensionContext CreateContext() =>
        new(new(Project, "rpc", false, true, new TestUi()), () => null, () => [], () => true);

    private sealed class TestUi : PiSharp.Runtime.Extensions.IPiSharpExtensionUi
    {
        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> InputAsync(string title, string? placeholder = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public void Notify(string message, string type = "info") { }
    }

    private sealed record Replacement(ExtensionCatalog Catalog, WeakReference PreviousContext, PiSharpExtensionProcessExecutor PreviousExecutor);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
