using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal sealed record PiSessionStartupTarget(string WorkingDirectory, CliArguments Arguments)
{
    private const long MaximumSessionBytes = 128L * 1024 * 1024;

    public static PiSessionStartupTarget Resolve(string invocationDirectory, CliArguments arguments)
    {
        var invocationPath = Path.GetFullPath(invocationDirectory);
        var sessionPath = ResolveSessionReference(arguments.SessionPath, invocationPath);
        var forkSource = ResolveSessionReference(arguments.ForkSource, invocationPath);
        var sourcePath = sessionPath ?? forkSource;
        var workingDirectory = sourcePath is null ? invocationPath : ReadWorkingDirectory(sourcePath, invocationPath);

        return new(workingDirectory, arguments with
        {
            SessionPath = sessionPath ?? arguments.SessionPath,
            ForkSource = forkSource ?? arguments.ForkSource,
            SessionDirectory = ResolveOptionalPath(arguments.SessionDirectory, invocationPath),
            FileArguments = ResolvePaths(arguments.FileArguments, invocationPath),
            SkillPaths = ResolvePaths(arguments.SkillPaths, invocationPath),
            PromptTemplatePaths = ResolvePaths(arguments.PromptTemplatePaths, invocationPath),
            ExtensionPaths = ResolvePaths(arguments.ExtensionPaths, invocationPath),
            SystemPrompt = ResolvePromptFile(arguments.SystemPrompt, invocationPath),
            AppendSystemPrompts = ResolvePromptFiles(arguments.AppendSystemPrompts, invocationPath)
        });
    }

    private static string? ResolveSessionReference(string? reference, string invocationPath)
    {
        if (reference is null || !LooksLikePath(reference)) return null;
        return Path.GetFullPath(reference, invocationPath);
    }

    private static bool LooksLikePath(string reference) =>
        reference.Contains(Path.DirectorySeparatorChar) || reference.Contains(Path.AltDirectorySeparatorChar) ||
        reference.EndsWith(".session.json", StringComparison.OrdinalIgnoreCase) ||
        reference.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);

    internal static string ReadWorkingDirectory(string sessionPath, string invocationPath)
    {
        if (!File.Exists(sessionPath)) return invocationPath;
        var file = new FileInfo(sessionPath);
        if (file.Length > MaximumSessionBytes)
            throw new InvalidDataException("Session exceeds the 128 MiB startup inspection limit.");

        var content = File.ReadAllText(sessionPath);
        var session = sessionPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? PiJsonlSessionInterchange.Import(content)
            : ConversationSession.Parse(content);
        var workingDirectory = Path.GetFullPath(session.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
            throw new InvalidDataException($"Session working directory does not exist: {workingDirectory}");
        return workingDirectory;
    }

    private static string? ResolveOptionalPath(string? path, string invocationPath) =>
        path is null ? null : Path.GetFullPath(path, invocationPath);

    private static IReadOnlyList<string>? ResolvePaths(IReadOnlyList<string>? paths, string invocationPath) =>
        paths?.Select(path => path is "~" || path.StartsWith("~/", StringComparison.Ordinal) ||
            path.StartsWith("~\\", StringComparison.Ordinal) ? path : Path.GetFullPath(path, invocationPath)).ToArray();

    private static string? ResolvePromptFile(string? input, string invocationPath)
    {
        if (input is null) return null;
        try
        {
            var path = Path.GetFullPath(input, invocationPath);
            return File.Exists(path) ? path : input;
        }
        catch (ArgumentException) { return input; }
    }

    private static IReadOnlyList<string>? ResolvePromptFiles(IReadOnlyList<string>? inputs, string invocationPath) =>
        inputs?.Select(input => ResolvePromptFile(input, invocationPath)!).ToArray();
}
