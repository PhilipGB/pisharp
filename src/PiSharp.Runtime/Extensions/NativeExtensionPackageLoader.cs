using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Resources;
using NativeContract = PiSharp.Extensions.Abstractions.IPiSharpExtension;

namespace PiSharp.Runtime.Extensions;

internal sealed record NativeExtensionPackageLoadResult(NativeExtensionPackageHandle? Handle, string? Error);

/// <summary>Owns one package's extension instances and collectible load context.</summary>
internal sealed class NativeExtensionPackageHandle : IDisposable
{
    private AssemblyLoadContext? _loadContext;
    private IReadOnlyList<NativeContract> _extensions;
    internal NativeExtensionProcessExecutor? ProcessExecutor { get; private set; }

    internal NativeExtensionPackageHandle(NativeExtensionPackageManifest manifest, ResourceSourceInfo sourceInfo,
        AssemblyLoadContext loadContext, IReadOnlyList<NativeContract> extensions)
    {
        Manifest = manifest;
        SourceInfo = sourceInfo;
        _loadContext = loadContext;
        _extensions = extensions;
        LoadContextReference = new(loadContext, trackResurrection: true);
    }

    internal NativeExtensionPackageManifest Manifest { get; }
    internal ResourceSourceInfo SourceInfo { get; }
    internal AssemblyLoadContext? LoadContext => _loadContext;
    internal IReadOnlyList<NativeContract> Extensions => _extensions;
    internal WeakReference LoadContextReference { get; }

    /// <summary>Releases entry instances after the host has captured their registrations.</summary>
    internal void ReleaseExtensions() => _extensions = [];

    internal void InitializeProcessExecutor(string workingDirectory, TimeSpan? unloadTimeout = null) =>
        ProcessExecutor = new(workingDirectory, unloadTimeout);

    internal void Unload()
    {
        try { ProcessExecutor?.Dispose(); }
        finally
        {
            ProcessExecutor = null;
            NativeExtensionFunctionCache.ReleaseDefaultOptionsDescriptors();
            ReleaseExtensions();
            var context = Interlocked.Exchange(ref _loadContext, null);
            context?.Unload();
        }
    }

    public void Dispose() => Unload();
}

/// <summary>Releases MEAI reflection descriptors that can otherwise root collectible extension methods.</summary>
internal static class NativeExtensionFunctionCache
{
    private static readonly FieldInfo? s_descriptorCacheField = typeof(AIFunctionFactory)
        .GetNestedType("ReflectionAIFunctionDescriptor", BindingFlags.NonPublic)?
        .GetField("_descriptorCache", BindingFlags.Static | BindingFlags.NonPublic);

    internal static void ReleaseDefaultOptionsDescriptors()
    {
        // MEAI caches method descriptors against its process-wide default serializer options. Drop that
        // bucket when a package unloads so the cache cannot retain MethodInfo objects from its ALC.
        var cache = s_descriptorCacheField?.GetValue(null);
        var remove = cache?.GetType().GetMethod("Remove", BindingFlags.Instance | BindingFlags.Public,
            binder: null, types: [typeof(JsonSerializerOptions)], modifiers: null);
        remove?.Invoke(cache, [AIJsonUtilities.DefaultOptions]);
    }
}

/// <summary>Loads explicitly declared compiled extension entry points from an extracted package.</summary>
internal static class NativeExtensionPackageLoader
{
    private static readonly Assembly s_contractAssembly = typeof(NativeContract).Assembly;
    private static readonly Assembly s_aiAbstractionsAssembly = typeof(AIFunction).Assembly;

    internal static NativeExtensionPackageLoadResult Load(string packageRoot, ResourceSourceInfo sourceInfo)
    {
        ArgumentNullException.ThrowIfNull(sourceInfo);

        // Read and validate compatibility before constructing an ALC or asking the CLR to load package code.
        NativeExtensionPackageManifest manifest;
        try
        {
            manifest = NativeExtensionPackageManifestReader.Read(packageRoot);
        }
        catch (Exception error) when (IsPackageReadFailure(error))
        {
            return new(null, error.Message);
        }

        NativeExtensionPackageLoadContext? context = null;
        try
        {
            context = new(manifest.Extensions.Select(entry => entry.AssemblyPath));
            var assemblies = new Dictionary<string, Assembly>(PathComparer);
            var extensions = new List<NativeContract>(manifest.Extensions.Count);
            foreach (var entry in manifest.Extensions)
            {
                if (!assemblies.TryGetValue(entry.AssemblyPath, out var assembly))
                {
                    assembly = context.LoadFromAssemblyPath(entry.AssemblyPath);
                    context.ValidateSharedAssemblyReferences(assembly, manifest.Id);
                    assemblies.Add(entry.AssemblyPath, assembly);
                }

                var type = assembly.GetType(entry.Type, throwOnError: false, ignoreCase: false);
                if (type is null)
                    throw new InvalidDataException($"Native extension package {manifest.Id} does not contain declared entry type " +
                        $"'{entry.Type}' in '{entry.Assembly}'.");
                if (!type.IsClass || type.IsAbstract || !(type.IsPublic || type.IsNestedPublic) ||
                    !typeof(NativeContract).IsAssignableFrom(type))
                    throw new InvalidDataException($"Native extension entry type '{entry.Type}' must be a public, concrete " +
                        $"implementation of {typeof(NativeContract).FullName}.");

                if (Activator.CreateInstance(type) is not NativeContract extension)
                    throw new InvalidDataException($"Native extension entry type '{entry.Type}' needs a public parameterless constructor.");
                extensions.Add(extension);
            }

            return new(new(manifest, sourceInfo, context, extensions), null);
        }
        catch (Exception error)
        {
            context?.Unload();
            var cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            return new(null, $"Failed to load native extension package {manifest.Id}: {cause.Message}");
        }
    }

    private static bool IsPackageReadFailure(Exception error) => error is InvalidDataException or IOException or
        UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class NativeExtensionPackageLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver[] _resolvers;

        internal NativeExtensionPackageLoadContext(IEnumerable<string> componentPaths)
            : base("PiSharp native extension package", isCollectible: true)
        {
            _resolvers = componentPaths.Distinct(PathComparer).Select(path => new AssemblyDependencyResolver(path)).ToArray();
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (HasSameName(assemblyName, s_contractAssembly.GetName()))
                return ResolveSharedAssembly(assemblyName, s_contractAssembly);
            if (HasSameName(assemblyName, s_aiAbstractionsAssembly.GetName()))
                return ResolveSharedAssembly(assemblyName, s_aiAbstractionsAssembly);

            // Resolve package-private dependencies before returning to the default context. This lets a
            // package-local version take precedence over an identically named host assembly.
            foreach (var resolver in _resolvers)
            {
                var path = resolver.ResolveAssemblyToPath(assemblyName);
                if (path is not null) return LoadFromAssemblyPath(path);
            }

            // Let the default context supply framework and other host-owned assemblies.
            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            foreach (var resolver in _resolvers)
            {
                var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
                if (path is not null) return LoadUnmanagedDllFromPath(path);
            }
            return IntPtr.Zero;
        }

        internal void ValidateSharedAssemblyReferences(Assembly component, string packageId)
        {
            foreach (var reference in component.GetReferencedAssemblies())
            {
                Assembly? shared = HasSameName(reference, s_contractAssembly.GetName()) ? s_contractAssembly :
                    HasSameName(reference, s_aiAbstractionsAssembly.GetName()) ? s_aiAbstractionsAssembly : null;
                if (shared is not null) _ = ResolveSharedAssembly(reference, shared, packageId);
            }
        }

        private static Assembly ResolveSharedAssembly(AssemblyName requested, Assembly sharedAssembly,
            string? packageId = null)
        {
            var sharedIdentity = sharedAssembly.GetName();
            if (requested.Version is not null && requested.Version != sharedIdentity.Version)
                throw new InvalidDataException($"Native extension package {packageId ?? "<unknown>"} references " +
                    $"incompatible shared assembly {requested.Name} v{requested.Version}; host provides v{sharedIdentity.Version}.");
            var requestedToken = requested.GetPublicKeyToken() ?? [];
            var sharedToken = sharedIdentity.GetPublicKeyToken() ?? [];
            if (!requestedToken.SequenceEqual(sharedToken))
                throw new InvalidDataException($"Native extension package {packageId ?? "<unknown>"} references " +
                    $"a different signing identity for shared assembly {requested.Name}.");
            return sharedAssembly;
        }

        private static bool HasSameName(AssemblyName requested, AssemblyName shared) =>
            StringComparer.OrdinalIgnoreCase.Equals(requested.Name, shared.Name);
    }
}
