using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Runtime.Extensions;
using YamlDotNet.Core;

namespace PiSharp.Runtime.Resources;

public sealed record ResourceSourceInfo(string Path, string Source, string Scope, string Origin, string? BaseDir);
public sealed record SkillResource(string Name, string Description, string Path, bool ExplicitOnly, ResourceSourceInfo SourceInfo);
public sealed record PromptResource(string Name, string Description, string Template, string Path, ResourceSourceInfo SourceInfo);

/// <summary>Discovers user resources; project auto-discovery requires trust, while explicit paths are caller-selected.</summary>
public sealed class ResourceCatalog
{
    public IReadOnlyList<SkillResource> Skills { get; }
    public IReadOnlyList<PromptResource> Prompts { get; }
    public IReadOnlyList<ResourceDiagnostic> SkillDiagnostics { get; }
    public IReadOnlyList<ResourceDiagnostic> PromptDiagnostics { get; }
    public IReadOnlyList<ResourceDiagnostic> Diagnostics { get; }

    private ResourceCatalog(List<SkillResource> skills, List<PromptResource> prompts,
        List<ResourceDiagnostic> skillDiagnostics, List<ResourceDiagnostic> promptDiagnostics)
    {
        Skills = skills;
        Prompts = prompts;
        SkillDiagnostics = skillDiagnostics;
        PromptDiagnostics = promptDiagnostics;
        Diagnostics = skillDiagnostics.Concat(promptDiagnostics).ToArray();
    }

    public static async Task<ResourceCatalog> LoadAsync(string cwd, string agentDirectory, bool trusted,
        CancellationToken cancellationToken = default, bool discoverSkills = true, bool discoverPrompts = true,
        IReadOnlyList<string>? additionalSkills = null, IReadOnlyList<string>? additionalPrompts = null,
        IReadOnlyList<string>? userSkills = null, IReadOnlyList<string>? projectSkills = null,
        IReadOnlyList<string>? userPrompts = null, IReadOnlyList<string>? projectPrompts = null,
        ExtensionResourceDiscovery? extensionResources = null)
    {
        var skills = new List<SkillResource>();
        var prompts = new List<PromptResource>();
        var skillDiagnostics = new List<ResourceDiagnostic>();
        var promptDiagnostics = new List<ResourceDiagnostic>();
        var skillFiles = new List<(string Path, ResourceSourceInfo Source)>();
        var promptFiles = new List<(string Path, ResourceSourceInfo Source)>();
        var seenSkills = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var seenPrompts = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        if (trusted)
        {
            AddConfiguredPaths(projectSkills, Path.Combine(cwd, ".pi"), "project", "skills", skillFiles, seenSkills);
            AddConfiguredPaths(projectPrompts, Path.Combine(cwd, ".pi"), "project", "prompts", promptFiles, seenPrompts);
            if (discoverSkills)
            {
                AddAutoPaths(Path.Combine(cwd, ".pi", "skills"), projectSkills, Path.Combine(cwd, ".pi"),
                    "project", "skills", skillFiles, seenSkills, skillPaths: true);
                AddAutoPaths(Path.Combine(cwd, ".agents", "skills"), projectSkills, Path.Combine(cwd, ".pi"),
                    "project", "skills", skillFiles, seenSkills, skillPaths: true);
            }
            if (discoverPrompts)
                AddAutoPaths(Path.Combine(cwd, ".pi", "prompts"), projectPrompts, Path.Combine(cwd, ".pi"),
                    "project", "prompts", promptFiles, seenPrompts);
        }

        AddConfiguredPaths(userSkills, agentDirectory, "user", "skills", skillFiles, seenSkills);
        AddConfiguredPaths(userPrompts, agentDirectory, "user", "prompts", promptFiles, seenPrompts);

        if (discoverSkills)
        {
            AddAutoPaths(Path.Combine(agentDirectory, "skills"), userSkills, agentDirectory,
                "user", "skills", skillFiles, seenSkills, skillPaths: true);
            AddAutoPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills"),
                userSkills, agentDirectory, "user", "skills", skillFiles, seenSkills, skillPaths: true);
        }

        if (discoverPrompts)
        {
            AddAutoPaths(Path.Combine(agentDirectory, "prompts"), userPrompts, agentDirectory,
                "user", "prompts", promptFiles, seenPrompts);
        }

        AddExtensionPaths(extensionResources?.SkillPaths, "skills", skillFiles, seenSkills);
        AddExtensionPaths(extensionResources?.PromptPaths, "prompts", promptFiles, seenPrompts);
        AddCliPaths(additionalSkills, "skill", cwd, skillFiles, seenSkills);
        AddCliPaths(additionalPrompts, "prompt template", cwd, promptFiles, seenPrompts);

        foreach (var (path, source) in skillFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadResourceAsync(path, source, skillDiagnostics, cancellationToken);
            if (text is null) continue;
            var declared = Path.GetFileName(path) == "SKILL.md";
            var metadata = ParseResource(text, path, source, skillDiagnostics, declared);
            if (metadata is null) continue;
            var name = metadata.Name;
            if (string.IsNullOrEmpty(name))
                name = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
            var description = metadata.Description ?? "";
            var hasDescription = ResourceFrontmatter.TrimBody(description).Length > 0;
            if (!declared && !hasDescription) continue;
            if (!hasDescription)
            {
                skillDiagnostics.Add(new("warning", "description is required", path, SourceInfo: source));
            }
            if (description.Length > 1024)
                skillDiagnostics.Add(new("warning", $"description exceeds 1024 characters ({description.Length})", path,
                    SourceInfo: source));
            if (name.Length > 64)
                skillDiagnostics.Add(new("warning", $"name exceeds 64 characters ({name.Length})", path,
                    SourceInfo: source));
            if (!Regex.IsMatch(name, "^[a-z0-9-]+$", RegexOptions.CultureInvariant))
                skillDiagnostics.Add(new("warning", "name contains invalid characters (must be lowercase a-z, 0-9, hyphens only)",
                    path, SourceInfo: source));
            if (name.StartsWith("-", StringComparison.Ordinal) || name.EndsWith("-", StringComparison.Ordinal))
                skillDiagnostics.Add(new("warning", "name must not start or end with a hyphen", path,
                    SourceInfo: source));
            if (name.Contains("--", StringComparison.Ordinal))
                skillDiagnostics.Add(new("warning", "name must not contain consecutive hyphens", path,
                    SourceInfo: source));
            if (!hasDescription) continue;
            var existing = skills.FirstOrDefault(item => item.Name == name);
            if (existing is not null)
            {
                skillDiagnostics.Add(new("collision", $"name \"{name}\" collision", path,
                    new("skill", name, existing.Path, path, existing.SourceInfo, source), source));
                continue;
            }
            skills.Add(new(name, description, path, metadata.DisableModelInvocation, source));
        }

        foreach (var (path, source) in promptFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(path);
            var existing = prompts.FirstOrDefault(item => item.Name == name);
            if (existing is not null)
            {
                promptDiagnostics.Add(new("collision", $"name \"{name}\" collision", path,
                    new("prompt", name, existing.Path, path, existing.SourceInfo, source), source));
                continue;
            }
            var text = await ReadResourceAsync(path, source, promptDiagnostics, cancellationToken);
            if (text is null) continue;
            var metadata = ParseResource(text, path, source, promptDiagnostics, reportErrors: true);
            if (metadata is null) continue;
            var body = metadata.Body;
            var description = metadata.Description;
            if (string.IsNullOrEmpty(description)) description = PromptDescription(body);
            prompts.Add(new(name, description, body, path, source));
        }

        return new(skills, prompts, skillDiagnostics, promptDiagnostics);

        void AddCliPaths(IReadOnlyList<string>? roots, string description, string baseDirectory,
            List<(string Path, ResourceSourceInfo Source)> target, HashSet<string> seen)
        {
            foreach (var root in roots ?? [])
            {
                var selected = LocalResourcePathRules.ResolvePath(root, baseDirectory);
                var isSkill = description == "skill";
                var source = new ResourceSourceInfo(Path.GetFullPath(selected), "local", "temporary", "top-level",
                    Path.GetDirectoryName(Path.GetFullPath(selected)));
                if (!LocalResourceFileType.ExistsFollowingLinks(selected))
                {
                    if (isSkill) skillDiagnostics.Add(new("warning", "skill path does not exist", selected, SourceInfo: source));
                    continue;
                }
                IReadOnlyList<string> paths;
                if (Directory.Exists(selected)) paths = EnumerateResourceFiles(selected, isSkill ? "skills" : "prompts");
                else if (LocalResourceFileType.IsRegularFile(selected) && selected.EndsWith(".md", StringComparison.Ordinal)) paths = [selected];
                else
                {
                    if (isSkill) skillDiagnostics.Add(new("warning", "skill path is not a markdown file", selected, SourceInfo: source));
                    continue;
                }
                foreach (var path in paths)
                {
                    AddPath(target, seen, path, source with { Path = Path.GetFullPath(path), BaseDir = Path.GetDirectoryName(Path.GetFullPath(path)) });
                }
            }
        }

        void AddConfiguredPaths(IReadOnlyList<string>? entries, string baseDirectory, string scope, string kind,
            List<(string Path, ResourceSourceInfo Source)> target, HashSet<string> seen)
        {
            var fullBase = Path.GetFullPath(baseDirectory);
            var candidates = LocalResourcePathRules.GetPaths(entries)
                .SelectMany(entry => EnumerateResourceFiles(LocalResourcePathRules.ResolvePath(entry, fullBase), kind,
                    recursive: kind == "prompts"))
                .Distinct(PathComparer()).ToArray();
            foreach (var path in LocalResourcePathRules.ApplyOverrides(candidates, entries, fullBase, skillPaths: kind == "skills"))
            {
                var source = new ResourceSourceInfo(Path.GetFullPath(path), "local", scope, "top-level", fullBase);
                AddPath(target, seen, path, source);
            }
        }

        void AddAutoPaths(string root, IReadOnlyList<string>? entries, string baseDirectory, string scope, string kind,
            List<(string Path, ResourceSourceInfo Source)> target, HashSet<string> seen, bool skillPaths = false)
        {
            var fullBase = Path.GetFullPath(baseDirectory);
            var candidates = EnumerateResourceFiles(root, kind);
            foreach (var path in LocalResourcePathRules.ApplyOverrides(candidates, entries, fullBase, skillPaths))
            {
                var source = ProjectSourceInfo(path, root, kind, cwd, agentDirectory, explicitRoot: false);
                AddPath(target, seen, path, source with { Scope = scope });
            }
        }

        static void AddPath(List<(string Path, ResourceSourceInfo Source)> target, HashSet<string> seen,
            string path, ResourceSourceInfo source)
        {
            var fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath)) target.Add((fullPath, source with { Path = fullPath }));
        }

        void AddExtensionPaths(IReadOnlyList<ExtensionDiscoveredResourcePath>? entries, string kind,
            List<(string Path, ResourceSourceInfo Source)> target, HashSet<string> seen)
        {
            foreach (var entry in entries ?? [])
            {
                var files = EnumerateResourceFiles(entry.Path, kind, recursive: kind == "prompts");
                foreach (var path in files)
                    AddPath(target, seen, path, entry.SourceInfo);
            }
        }
    }

    private static IReadOnlyList<string> EnumerateResourceFiles(string path, string kind, bool recursive = false)
    {
        if (File.Exists(path))
        {
            try { if (!LocalResourceFileType.IsRegularFile(path)) return []; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
            var valid = kind switch
            {
                "skills" => Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase),
                "prompts" => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            return valid ? [Path.GetFullPath(path)] : [];
        }
        if (!Directory.Exists(path)) return [];

        var result = new List<string>();
        var pending = new Stack<string>();
        var visitedDirectories = 0;
        pending.Push(Path.GetFullPath(path));
        while (pending.Count > 0 && result.Count < 1000 && visitedDirectories++ < 10_000)
        {
            var directory = pending.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith(".", StringComparison.Ordinal) || name.Equals("node_modules", StringComparison.Ordinal))
                    continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0 && (kind == "skills" || recursive))
                        pending.Push(entry);
                    continue;
                }
                var match = kind switch
                {
                    "skills" => name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase),
                    "prompts" => Path.GetExtension(name).Equals(".md", StringComparison.OrdinalIgnoreCase),
                    _ => false
                };
                if (match)
                {
                    try { if (LocalResourceFileType.IsRegularFile(entry)) result.Add(Path.GetFullPath(entry)); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
                }
                if (result.Count == 1000) break;
            }
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public string SystemInstructions(string? skillFileReadTool = "read")
    {
        if (skillFileReadTool is not ("read" or "bash" or "indirect")) return "";
        var visible = Skills.Where(skill => !skill.ExplicitOnly).ToArray();
        if (visible.Length == 0) return "";

        var readInstruction = skillFileReadTool switch
        {
            "read" => "Use the read tool to load a skill's file when the task matches its description.",
            "bash" => "Use bash to load a skill's file when the task matches its description.",
            _ => "Load a skill's file when the task matches its description."
        };
        var lines = new List<string>
        {
            "The following skills provide specialized instructions for specific tasks.",
            readInstruction,
            "When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.",
            "",
            "<available_skills>"
        };
        foreach (var skill in visible)
        {
            lines.Add("  <skill>");
            lines.Add($"    <name>{EscapeXml(skill.Name)}</name>");
            lines.Add($"    <description>{EscapeXml(skill.Description)}</description>");
            lines.Add($"    <location>{EscapeXml(skill.Path)}</location>");
            lines.Add("  </skill>");
        }
        lines.Add("</available_skills>");
        return $"<skills>\n{string.Join('\n', lines)}\n</skills>";
    }

    public async Task<string> InvokeSkillAsync(string name, string arguments, CancellationToken cancellationToken = default)
    {
        var skill = Skills.SingleOrDefault(item => item.Name == name) ?? throw new ArgumentException($"Unknown skill: {name}");
        var body = ResourceFrontmatter.Parse(await ReadTextAsync(skill.Path, cancellationToken)).Body;
        var skillDirectory = Path.GetDirectoryName(skill.Path) ?? Path.GetFullPath(".");
        var skillBlock = $"<skill name=\"{skill.Name}\" location=\"{skill.Path}\">\n" +
            $"References are relative to {skillDirectory}.\n\n{ResourceFrontmatter.TrimBody(body)}\n</skill>";
        var trimmedArguments = arguments.Trim();
        return trimmedArguments.Length == 0 ? skillBlock : $"{skillBlock}\n\n{trimmedArguments}";
    }

    public async Task<string> ResolveInputAsync(string input, CancellationToken cancellationToken = default)
    {
        if (!input.StartsWith('/')) return input;
        var separator = input.IndexOfAny([' ', '\n', '\t']);
        var command = separator < 0 ? input : input[..separator];
        var arguments = separator < 0 ? "" : input[(separator + 1)..].Trim();
        if (command.StartsWith("/skill:", StringComparison.Ordinal))
            return await InvokeSkillAsync(command[7..], arguments, cancellationToken);
        return Prompts.Any(prompt => "/" + prompt.Name == command) ? ExpandPrompt(command[1..], arguments) : input;
    }

    public string ExpandPrompt(string name, string arguments)
    {
        var prompt = Prompts.SingleOrDefault(item => item.Name == name) ?? throw new ArgumentException($"Unknown template: {name}");
        var parts = Tokenize(arguments);
        var all = string.Join(' ', parts);
        return Regex.Replace(prompt.Template, @"\$\{(@|\d+)(?::-(.*?))?\}|\$(ARGUMENTS|@|[1-9]\d*)", match =>
        {
            var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
            var result = key is "@" or "ARGUMENTS" ? all : int.TryParse(key, out var index) && index > 0 && index <= parts.Count ? parts[index - 1] : "";
            return result.Length > 0 ? result : match.Groups[2].Value;
        });
    }

    private static List<string> Tokenize(string arguments)
    {
        var result = new List<string>();
        var word = new StringBuilder();
        char quote = '\0';
        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (c == '\\' && i + 1 < arguments.Length) { word.Append(arguments[++i]); continue; }
            if (c is '\'' or '"') { if (quote == c) quote = '\0'; else if (quote == '\0') quote = c; else word.Append(c); continue; }
            if (char.IsWhiteSpace(c) && quote == '\0')
            { if (word.Length > 0) { result.Add(word.ToString()); word.Clear(); } }
            else word.Append(c);
        }
        if (quote != '\0') throw new ArgumentException("Unclosed quote in template arguments.");
        if (word.Length > 0) result.Add(word.ToString());
        return result;
    }

    private static ResourceSourceInfo ProjectSourceInfo(string path, string selectedRoot, string kind,
        string cwd, string agentDirectory, bool explicitRoot)
    {
        var fullPath = Path.GetFullPath(path);
        if (explicitRoot)
            return new(fullPath, "local", "temporary", "top-level", Path.GetDirectoryName(fullPath));

        var userRoot = Path.Combine(agentDirectory, kind);
        if (IsUnder(fullPath, userRoot))
            return new(fullPath, "auto", "user", "top-level", Path.GetFullPath(agentDirectory));

        var userAgentsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents");
        if (IsUnder(fullPath, Path.Combine(userAgentsRoot, kind)))
            return new(fullPath, "auto", "user", "top-level", Path.GetFullPath(userAgentsRoot));

        var projectRoot = Path.Combine(cwd, ".pi", kind);
        if (IsUnder(fullPath, projectRoot))
            return new(fullPath, "auto", "project", "top-level", Path.GetFullPath(Path.Combine(cwd, ".pi")));

        var projectAgentsRoot = Path.Combine(cwd, ".agents");
        if (IsUnder(fullPath, Path.Combine(projectAgentsRoot, kind)))
            return new(fullPath, "auto", "project", "top-level", Path.GetFullPath(projectAgentsRoot));

        var baseDir = Directory.Exists(selectedRoot) ? Path.GetFullPath(selectedRoot) : Path.GetDirectoryName(fullPath);
        return new(fullPath, "local", "temporary", "top-level", baseDir);
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string PromptDescription(string body)
    {
        var firstLine = body.Split('\n').FirstOrDefault(line => ResourceFrontmatter.TrimBody(line).Length > 0) ?? "";
        return firstLine.Length > 60 ? firstLine[..60] + "..." : firstLine;
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    private static ResourceFrontmatter? ParseResource(string text, string path, ResourceSourceInfo source,
        List<ResourceDiagnostic> diagnostics, bool reportErrors)
    {
        try { return ResourceFrontmatter.Parse(text); }
        catch (YamlException error)
        {
            if (reportErrors) diagnostics.Add(new("warning", error.Message, path, SourceInfo: source));
            return null;
        }
    }

    private static async Task<string?> ReadResourceAsync(string path, ResourceSourceInfo source,
        List<ResourceDiagnostic> diagnostics, CancellationToken token)
    {
        try { return await ReadTextAsync(path, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            token.ThrowIfCancellationRequested();
            diagnostics.Add(new("warning", error.Message, path, SourceInfo: source));
            return null;
        }
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken token) =>
        Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, token));
}
