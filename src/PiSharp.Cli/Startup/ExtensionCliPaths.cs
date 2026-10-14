using PiSharp.Runtime.Resources;

namespace PiSharp.Cli.Startup;

internal static class ExtensionCliPaths
{
    internal static CliArguments Resolve(CliArguments arguments, string invocationDirectory) =>
        arguments.ExtensionPaths is null ? arguments : arguments with
        {
            // Pi resolves local CLI arguments before the resource loader trims their resulting paths.
            ExtensionPaths = arguments.ExtensionPaths.Select(path => ResourceLocalPath.IsLocal(path)
                ? ResourceLocalPath.Resolve(path, invocationDirectory, trim: false) : path).ToArray()
        };
}
