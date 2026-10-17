using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class NativeExtensionProviderAlcTests
{
    [Fact]
    public async Task ReplacedProviderFactoryAndClientReleaseThePreviousPackageLoadContext()
    {
        using var temporary = new TemporaryDirectory();
        var workingDirectory = Path.Combine(temporary.Path, "project");
        var agentDirectory = Path.Combine(temporary.Path, "agent");
        var packages = Path.Combine(temporary.Path, "packages");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(packages);

        var v1 = await ResourcePackageCommandProcessTests.PackExternalNativeModelFixtureAsync(
            Path.Combine(packages, "v1"));
        var v2 = await ResourcePackageCommandProcessTests.PackExternalNativeModelFixtureAsync(
            Path.Combine(packages, "v2"), revision: "v2", version: "2.0.0");
        var weakContext = LoadAndReplaceProvider(workingDirectory, agentDirectory, v1, v2);

        AssertContextUnloaded(weakContext);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndReplaceProvider(string workingDirectory,
        string agentDirectory, string v1Package, string v2Package)
    {
        var store = new NativeExtensionPackageStore(agentDirectory);
        var installedV1 = store.InstallAsync(workingDirectory, v1Package, "user").GetAwaiter().GetResult();
        var installedV2 = store.InstallAsync(workingDirectory, v2Package, "user").GetAwaiter().GetResult();
        var v1Roots = store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV1])
            .GetAwaiter().GetResult();
        var v2Roots = store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV2])
            .GetAwaiter().GetResult();
        var catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: v1Roots);
        var previousContext = Assert.Single(catalog.NativePackageLoadContextReferences);
        using var http = new HttpClient();
        var runtime = ProviderModelRuntime.CreateAsync(agentDirectory, false, _ => null, http, offline: true)
            .GetAwaiter().GetResult();
        runtime.SetExtensionProviders(catalog.Registration.ExtensionProviders);
        var oldClient = ProviderChatClientFactory.Create(runtime.ResolveAsync("native_fixture", "physical-main")
            .GetAwaiter().GetResult());
        Assert.Equal("native-fixture-provider-response",
            oldClient.GetResponseAsync([new ChatMessage(ChatRole.User, "before replacement")])
                .GetAwaiter().GetResult().Text);

        catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true, preloaded: catalog,
            nativeExtensionPackageRoots: v2Roots);
        runtime.SetExtensionProviders(catalog.Registration.ExtensionProviders);

        var removedError = Assert.Throws<InvalidOperationException>(() =>
            oldClient.GetResponseAsync([new ChatMessage(ChatRole.User, "after replacement")])
                .GetAwaiter().GetResult());
        Assert.Contains("removed or replaced", removedError.Message, StringComparison.Ordinal);
        var models = runtime.ListModelsAsync("native_fixture").GetAwaiter().GetResult();
        Assert.Contains(models, model => model.Id == "physical-main-v2");
        Assert.DoesNotContain(models, model => model.Id == "physical-main");

        catalog.Dispose();
        runtime.SetExtensionProviders([]);
        return previousContext;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertContextUnloaded(WeakReference context)
    {
        for (var attempt = 0; attempt < 30 && context.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }

        Assert.False(context.IsAlive,
            "A replaced provider factory or chat client kept its native package load context alive.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "pisharp-native-provider-alc-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
