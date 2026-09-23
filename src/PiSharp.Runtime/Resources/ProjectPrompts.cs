namespace PiSharp.Runtime.Resources;

/// <summary>System-prompt resources are privileged: project copies require an explicit trust decision.</summary>
public static class ProjectPrompts
{
    public static async Task<(string? System, string? Append)> LoadAsync(string cwd, string agentDirectory,
        bool projectTrusted, CancellationToken cancellationToken = default)
    {
        async Task<string?> Read(string directory, string name)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException($"Prompt exceeds 64KB: {path}");
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 64 * 1024)
                throw new InvalidDataException($"Prompt exceeds 64KB: {path}");
            return text.TrimStart('\uFEFF');
        }
        var global = Path.GetFullPath(agentDirectory);
        var project = Path.Combine(Path.GetFullPath(cwd), ".pi");
        var system = projectTrusted ? await Read(project, "SYSTEM.md") : null;
        system ??= await Read(global, "SYSTEM.md");
        var globalAppend = await Read(global, "APPEND_SYSTEM.md");
        var projectAppend = projectTrusted ? await Read(project, "APPEND_SYSTEM.md") : null;
        return (system, string.Join("\n\n", new[] { globalAppend, projectAppend }.Where(value => !string.IsNullOrEmpty(value))));
    }
}
