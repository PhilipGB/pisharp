using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli.Startup;

internal sealed class ExtensionStartupFailure(IReadOnlyList<ExtensionLoadError> errors) : IOException(
    string.Join(Environment.NewLine, errors.Select(error => $"Error: Failed to load extension \"{error.Path}\": {error.Error}")) +
    Environment.NewLine + "Hint: Start without extensions using \"pisharp -ne\".");
