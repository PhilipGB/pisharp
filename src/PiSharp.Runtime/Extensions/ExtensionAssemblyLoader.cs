using System.Reflection;
using System.Runtime.Loader;
using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

internal static class ExtensionAssemblyLoader
{
    internal static (AssemblyLoadContext? Context, string? Error) Load(string path,
        ResourceSourceInfo sourceInfo, ExtensionRegistration registration)
    {
        PluginLoadContext? context = null;
        var restore = registration.CreateConfigurationCheckpoint();
        try
        {
            context = new PluginLoadContext(path);
            var assembly = context.LoadFromAssemblyPath(path);
            var types = assembly.GetExportedTypes().Where(type =>
                typeof(IPiSharpExtension).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface).ToArray();
            if (types.Length == 0)
                throw new InvalidDataException($"Extension does not export an IPiSharpExtension implementation: {path}");
            registration.SetCurrentSourceInfo(sourceInfo);
            foreach (var type in types)
            {
                if (Activator.CreateInstance(type) is not IPiSharpExtension extension)
                    throw new InvalidDataException($"Extension {type.FullName} needs a public parameterless constructor.");
                extension.Configure(registration);
            }
            return (context, null);
        }
        catch (Exception error)
        {
            restore();
            context?.Unload();
            var cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            return (null, "Failed to load extension: " + cause.Message);
        }
        finally { registration.SetCurrentSourceInfo(null); }
    }

    private sealed class PluginLoadContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        protected override Assembly? Load(AssemblyName name)
        {
            if (Default.Assemblies.Any(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), name)))
                return null; // Share PiSharp and MEAI contracts with the host.
            var resolved = _resolver.ResolveAssemblyToPath(name);
            return resolved is null ? null : LoadFromAssemblyPath(resolved);
        }
    }
}
