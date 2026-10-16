using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using NativeContract = PiSharp.Extensions.Abstractions.IPiSharpExtension;

namespace PiSharp.Tests.Runtime.Extensions;

public sealed class NativeExtensionPackageLoaderTests
{
    private static readonly string s_fixtures = Path.Combine(AppContext.BaseDirectory, "NativeExtensionLoaderFixtures");

    [Fact]
    public void LoaderUsesDeclaredEntryAndSharesHostApiIdentity()
    {
        using var package = CreatePackage("V1", "loader.identity", "1.0.0");
        var result = Load(package.Path);

        Assert.True(result.Error is null, result.Error);
        var handle = Assert.IsType<NativeExtensionPackageHandle>(result.Handle);
        var extension = Assert.Single(handle.Extensions);
        var extensionAssembly = extension.GetType().Assembly;
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(extensionAssembly));
        var contract = Assert.Single(extension.GetType().GetInterfaces(), type => type == typeof(NativeContract));
        Assert.Same(typeof(NativeContract).Assembly, contract.Assembly);
        handle.Unload();
    }

    [Fact]
    public void IncompatibleApiVersionIsRejectedBeforeEntryConstruction()
    {
        using var package = CreatePackage("V1", "loader.incompatible", "1.0.0", apiVersion: 2);
        var marker = Path.Combine(package.Path, "activated.txt");
        var prior = Environment.GetEnvironmentVariable("PISHARP_NATIVE_LOADER_ACTIVATION_MARKER");
        try
        {
            Environment.SetEnvironmentVariable("PISHARP_NATIVE_LOADER_ACTIVATION_MARKER", marker);
            var result = Load(package.Path);

            Assert.Null(result.Handle);
            Assert.Contains("requires PiSharp extension API v2", result.Error, StringComparison.Ordinal);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PISHARP_NATIVE_LOADER_ACTIVATION_MARKER", prior);
        }
    }

    [Fact]
    public void IncompatibleSharedAssemblyIdentityIsRejectedBeforeEntryConstruction()
    {
        using var package = CreateIncompatibleContractPackage();
        var marker = Path.Combine(package.Path, "activated.txt");
        var prior = Environment.GetEnvironmentVariable("PISHARP_NATIVE_LOADER_ACTIVATION_MARKER");
        try
        {
            Environment.SetEnvironmentVariable("PISHARP_NATIVE_LOADER_ACTIVATION_MARKER", marker);
            var result = Load(package.Path);

            Assert.Null(result.Handle);
            Assert.Contains("incompatible shared assembly PiSharp.Extensions.Abstractions v2.0.0.0", result.Error,
                StringComparison.Ordinal);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PISHARP_NATIVE_LOADER_ACTIVATION_MARKER", prior);
        }
    }

    [Fact]
    public void MissingMalformedAndUnsafeManifestEntriesAreRejected()
    {
        using var directory = new TemporaryDirectory();
        Assert.Contains("manifest is missing", Assert.Throws<InvalidDataException>(() =>
            NativeExtensionPackageManifestReader.Read(directory.Path)).Message, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(Path.Combine(directory.Path, NativeExtensionPackageManifestReader.FileName), "{");
        Assert.Contains("invalid JSON", Assert.Throws<InvalidDataException>(() =>
            NativeExtensionPackageManifestReader.Read(directory.Path)).Message, StringComparison.Ordinal);

        WriteManifest(directory.Path, "loader.unsafe", "1.0.0", 1, "../escape.dll",
            "PiSharp.Test.NativeLoaderExtension.V1.LoaderExtension");
        Assert.Contains("must not contain", Assert.Throws<InvalidDataException>(() =>
            NativeExtensionPackageManifestReader.Read(directory.Path)).Message, StringComparison.Ordinal);

        WriteManifest(directory.Path, "loader.bad-version", "released", 1, "lib/net10.0/extension.dll",
            "PiSharp.Test.NativeLoaderExtension.V1.LoaderExtension");
        Assert.Contains("valid numeric package version", Assert.Throws<InvalidDataException>(() =>
            NativeExtensionPackageManifestReader.Read(directory.Path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestReaderBoundsSizeAndNestingDepth()
    {
        using var directory = new TemporaryDirectory();
        var manifestPath = Path.Combine(directory.Path, NativeExtensionPackageManifestReader.FileName);
        File.WriteAllText(manifestPath, new string(' ', NativeExtensionPackageManifestReader.MaximumManifestBytes + 1));
        var oversized = Assert.Throws<InvalidDataException>(() => NativeExtensionPackageManifestReader.Read(directory.Path));
        Assert.Contains(NativeExtensionPackageManifestReader.MaximumManifestBytes.ToString(), oversized.Message,
            StringComparison.Ordinal);

        var nested = new string('[', NativeExtensionPackageManifestReader.MaximumManifestDepth) + "0" +
            new string(']', NativeExtensionPackageManifestReader.MaximumManifestDepth);
        File.WriteAllText(manifestPath, "{\"ignored\":" + nested + "}");
        var excessiveDepth = Assert.Throws<InvalidDataException>(() =>
            NativeExtensionPackageManifestReader.Read(directory.Path));
        Assert.Contains("invalid JSON", excessiveDepth.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderReportsMissingEntryTypeAndInvalidEntryContract()
    {
        using var missingTypePackage = CreatePackage("V1", "loader.missing-type", "1.0.0");
        WriteManifest(missingTypePackage.Path, "loader.missing-type", "1.0.0", 1,
            "lib/net10.0/PiSharp.Test.NativeLoaderExtension.V1.dll",
            "PiSharp.Test.NativeLoaderExtension.V1.DoesNotExist");
        var missingType = Load(missingTypePackage.Path);
        Assert.Null(missingType.Handle);
        Assert.Contains("does not contain declared entry type", missingType.Error, StringComparison.Ordinal);

        using var invalidContractPackage = CreatePackage("V1", "loader.invalid-entry", "1.0.0");
        WriteManifest(invalidContractPackage.Path, "loader.invalid-entry", "1.0.0", 1,
            "lib/net10.0/PiSharp.Test.NativeLoaderExtension.V1.dll",
            "PiSharp.Test.NativeLoaderExtension.V1.NonExtensionType");
        var invalidContract = Load(invalidContractPackage.Path);
        Assert.Null(invalidContract.Handle);
        Assert.Contains("must be a public, concrete implementation", invalidContract.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateDependenciesResolveIndependentlyAndPackageContextsUnload()
    {
        using var first = CreatePackage("V1", "loader.private-v1", "1.0.0");
        using var second = CreatePackage("V2", "loader.private-v2", "1.0.0");
        var hostDependency = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(s_fixtures, "V2",
            "PiSharp.Test.NativePrivateDependency.dll"));
        Assert.Equal("dependency-v2", hostDependency.GetType("PiSharp.Test.NativePrivateDependency.DependencyIdentity")!
            .GetProperty("Value")!.GetValue(null));

        var firstVersion = LoadReadAndUnload(first.Path, out var firstContext);
        var secondVersion = LoadReadAndUnload(second.Path, out var secondContext);

        Assert.Equal("dependency-v1", firstVersion);
        Assert.Equal("dependency-v2", secondVersion);
        Assert.Same(hostDependency, Assert.Single(AssemblyLoadContext.Default.Assemblies, assembly =>
            assembly.GetName().Name == "PiSharp.Test.NativePrivateDependency"));
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, assembly =>
            assembly.GetName().Name == typeof(NativeContract).Assembly.GetName().Name &&
            !ReferenceEquals(assembly, typeof(NativeContract).Assembly));

        AssertContextUnloaded(firstContext);
        AssertContextUnloaded(secondContext);
    }

    [Fact]
    public void ConstructorFailureReturnsDiagnosticAndUnloadsItsContext()
    {
        using var package = CreatePackage("V1", "loader.constructor-failure", "1.0.0");
        WriteManifest(package.Path, "loader.constructor-failure", "1.0.0", 1,
            "lib/net10.0/PiSharp.Test.NativeLoaderExtension.V1.dll",
            "PiSharp.Test.NativeLoaderExtension.V1.ThrowingExtension");

        var result = Load(package.Path);

        Assert.Null(result.Handle);
        Assert.Contains("fixture activation failure", result.Error, StringComparison.Ordinal);
    }

    private static NativeExtensionPackageLoadResult Load(string packageRoot) =>
        NativeExtensionPackageLoader.Load(packageRoot,
            new ResourceSourceInfo(packageRoot, "fixture", "user", "package", packageRoot));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string LoadReadAndUnload(string packageRoot, out WeakReference contextReference)
    {
        var result = Load(packageRoot);
        Assert.True(result.Error is null, result.Error);
        var handle = Assert.IsType<NativeExtensionPackageHandle>(result.Handle);
        contextReference = handle.LoadContextReference;
        var extension = Assert.Single(handle.Extensions);
        var version = Assert.IsType<string>(extension.GetType().GetProperty("PrivateDependencyVersion")!
            .GetValue(extension));
        handle.Unload();
        return version;
    }

    private static TemporaryDirectory CreatePackage(string fixtureVersion, string id, string version, int apiVersion = 1)
    {
        var fixtureRoot = Path.Combine(s_fixtures, fixtureVersion);
        var assemblyName = fixtureVersion == "V1"
            ? "PiSharp.Test.NativeLoaderExtension.V1"
            : "PiSharp.Test.NativeLoaderExtension.V2";
        var package = new TemporaryDirectory("pisharp-native-package-");
        var packageRoot = package.Path;
        var libraryDirectory = Directory.CreateDirectory(Path.Combine(packageRoot, "lib", "net10.0")).FullName;
        foreach (var fileName in new[]
                 {
                     assemblyName + ".dll", assemblyName + ".deps.json",
                     "PiSharp.Test.NativePrivateDependency.dll", "PiSharp.Extensions.Abstractions.dll"
                 })
            File.Copy(Path.Combine(fixtureRoot, fileName), Path.Combine(libraryDirectory, fileName));

        WriteManifest(packageRoot, id, version, apiVersion, "lib/net10.0/" + assemblyName + ".dll",
            fixtureVersion == "V1"
                ? "PiSharp.Test.NativeLoaderExtension.V1.LoaderExtension"
                : "PiSharp.Test.NativeLoaderExtension.V2.LoaderExtension");
        return package;
    }

    private static TemporaryDirectory CreateIncompatibleContractPackage()
    {
        const string assemblyName = "PiSharp.Test.NativeIncompatibleContract";
        var package = new TemporaryDirectory("pisharp-native-package-");
        var libraryDirectory = Directory.CreateDirectory(Path.Combine(package.Path, "lib", "net10.0")).FullName;
        foreach (var fileName in new[]
                 {
                     assemblyName + ".dll", assemblyName + ".deps.json", "PiSharp.Extensions.Abstractions.dll"
                 })
            File.Copy(Path.Combine(s_fixtures, "IncompatibleContract", fileName),
                Path.Combine(libraryDirectory, fileName));
        WriteManifest(package.Path, "loader.incompatible-identity", "1.0.0", 1,
            "lib/net10.0/" + assemblyName + ".dll", "PiSharp.Test.NativeIncompatibleContract.IncompatibleExtension");
        return package;
    }

    private static void WriteManifest(string packageRoot, string id, string version, int apiVersion,
        string assembly, string type)
    {
        var manifest = new
        {
            id,
            version,
            apiVersion,
            extensions = new[] { new { assembly, type } }
        };
        File.WriteAllText(Path.Combine(packageRoot, NativeExtensionPackageManifestReader.FileName),
            JsonSerializer.Serialize(manifest));
    }

    private static void AssertContextUnloaded(WeakReference context)
    {
        for (var attempt = 0; attempt < 20 && context.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(context.IsAlive, "The native extension load context remained reachable after unload.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory(string prefix = "pisharp-native-manifest-") =>
            Path = Directory.CreateTempSubdirectory(prefix).FullName;

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
