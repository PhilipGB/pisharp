using System.Text;
namespace PiSharp.Runtime.Resources;

/// <summary>Context-file discovery like Pi: one prioritized file per directory from home/agent
/// and each ancestor of cwd. Does not execute or load project extensions/settings.</summary>
public static class ContextInstructions
{
    private static readonly string[] s_names = ["AGENTS.override.md", "AGENTS.md", "AGENTS.MD", "CLAUDE.md", "CLAUDE.MD"];
    private const int MaxFileBytes = 64 * 1024;
    private const int MaxTotalBytes = 256 * 1024;

    public static async Task<string> LoadAsync(string workingDirectory, string agentDirectory,
        CancellationToken cancellationToken = default)
    {
        var folders = new List<string> { Path.GetFullPath(agentDirectory) };
        var parents = new Stack<string>();
        for (var folder = new DirectoryInfo(Path.GetFullPath(workingDirectory)); folder is not null; folder = folder.Parent)
            parents.Push(folder.FullName);
        while (parents.TryPop(out var directory))
            if (!folders.Contains(directory, StringComparer.Ordinal)) folders.Add(directory);
        var contents = new StringBuilder();
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = s_names.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
            if (file is null) continue;
            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes) throw new InvalidDataException($"Instruction file exceeds 64KB: {file}");
            var text = await File.ReadAllTextAsync(file, cancellationToken);
            if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes) throw new InvalidDataException($"Instruction file exceeds 64KB: {file}");
            var section = $"<project_instructions path=\"{XmlEscape(file)}\">\n{text.TrimStart('\uFEFF')}\n</project_instructions>\n\n";
            if (Encoding.UTF8.GetByteCount(contents.ToString()) + Encoding.UTF8.GetByteCount(section) > MaxTotalBytes)
                throw new InvalidDataException("Combined context instructions exceed 256KB.");
            contents.Append(section);
        }
        return contents.ToString();
    }

    private static string XmlEscape(string input) => System.Security.SecurityElement.Escape(input) ?? "";
}
