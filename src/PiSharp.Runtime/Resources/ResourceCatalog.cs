using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Resources;

public sealed record ResourceSourceInfo(string Path, string Source, string Scope, string Origin, string? BaseDir);
public sealed record SkillResource(string Name, string Description, string Path, bool ExplicitOnly, ResourceSourceInfo SourceInfo);
public sealed record PromptResource(string Name, string Description, string Template, string Path, ResourceSourceInfo SourceInfo);

/// <summary>Discovers bounded user resources; project auto-discovery requires trust, while explicit paths are caller-selected.</summary>
public sealed class ResourceCatalog
{
    public IReadOnlyList<SkillResource> Skills { get; }
    public IReadOnlyList<PromptResource> Prompts { get; }

    private ResourceCatalog(List<SkillResource> skills, List<PromptResource> prompts) => (Skills, Prompts) = (skills, prompts);

    public static async Task<ResourceCatalog> LoadAsync(string cwd, string agentDirectory, bool trusted,
        CancellationToken cancellationToken = default, bool discoverSkills = true, bool discoverPrompts = true,
        IReadOnlyList<string>? additionalSkills = null, IReadOnlyList<string>? additionalPrompts = null,
        IReadOnlyList<string>? userSkills = null, IReadOnlyList<string>? projectSkills = null,
        IReadOnlyList<string>? userPrompts = null, IReadOnlyList<string>? projectPrompts = null,
        ExtensionResourceDiscovery? extensionResources = null)
    {
        var skills = new List<SkillResource>();
        var prompts = new List<PromptResource>();
        var skillFiles = new List<(string Path, ResourceSourceInfo Source)>();
        var promptFiles = new List<(string Path, ResourceSourceInfo Source)>();
        var seenSkills = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var seenPrompts = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        AddCliPaths(additionalSkills, "skill", cwd, skillFiles, seenSkills);
        AddCliPaths(additionalPrompts, "prompt template", cwd, promptFiles, seenPrompts);

        if (trusted)
        {
            AddConfiguredPaths(projectSkills, Path.Combine(cwd, ".pi"), "project", "skills", skillFiles, seenSkills);
            AddConfiguredPaths(projectPrompts, Path.Combine(cwd, ".pi"), "project", "prompts", promptFiles, seenPrompts);
        }
        AddConfiguredPaths(userSkills, agentDirectory, "user", "skills", skillFiles, seenSkills);
        AddConfiguredPaths(userPrompts, agentDirectory, "user", "prompts", promptFiles, seenPrompts);

        if (discoverSkills)
        {
            if (trusted)
            {
                AddAutoPaths(Path.Combine(cwd, ".pi", "skills"), projectSkills, Path.Combine(cwd, ".pi"),
                    "project", "skills", skillFiles, seenSkills, skillPaths: true);
                AddAutoPaths(Path.Combine(cwd, ".agents", "skills"), projectSkills, Path.Combine(cwd, ".pi"),
                    "project", "skills", skillFiles, seenSkills, skillPaths: true);
            }
            AddAutoPaths(Path.Combine(agentDirectory, "skills"), userSkills, agentDirectory,
                "user", "skills", skillFiles, seenSkills, skillPaths: true);
            AddAutoPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills"),
                userSkills, agentDirectory, "user", "skills", skillFiles, seenSkills, skillPaths: true);
        }

        if (discoverPrompts)
        {
            if (trusted)
                AddAutoPaths(Path.Combine(cwd, ".pi", "prompts"), projectPrompts, Path.Combine(cwd, ".pi"),
                    "project", "prompts", promptFiles, seenPrompts);
            AddAutoPaths(Path.Combine(agentDirectory, "prompts"), userPrompts, agentDirectory,
                "user", "prompts", promptFiles, seenPrompts);
        }

        AddExtensionPaths(extensionResources?.SkillPaths, "skills", skillFiles, seenSkills);
        AddExtensionPaths(extensionResources?.PromptPaths, "prompts", promptFiles, seenPrompts);

        foreach (var (path, source) in skillFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (metadata, _) = Parse(await ReadBoundedAsync(path, cancellationToken));
            if (!metadata.TryGetValue("name", out var name) ||
                !Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant) || name.Length > 64 ||
                !metadata.TryGetValue("description", out var description) || description.Length is 0 or > 1024 ||
                skills.Any(item => item.Name == name)) continue;
            skills.Add(new(name, description, path, metadata.GetValueOrDefault("disable-model-invocation") == "true", source));
        }

        foreach (var (path, source) in promptFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(path);
            if (!Regex.IsMatch(name, "^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant) ||
                prompts.Any(item => item.Name == name)) continue;
            var (metadata, body) = Parse(await ReadBoundedAsync(path, cancellationToken));
            var description = metadata.GetValueOrDefault("description");
            if (string.IsNullOrEmpty(description)) description = PromptDescription(body);
            prompts.Add(new(name, description, body, path, source));
        }

        return new(skills, prompts);

        void AddCliPaths(IReadOnlyList<string>? roots, string description, string baseDirectory,
            List<(string Path, ResourceSourceInfo Source)> target, HashSet<string> seen)
        {
            foreach (var root in roots ?? [])
            {
                var selected = LocalResourcePathRules.ResolvePath(root, baseDirectory);
                if (!Directory.Exists(selected) && !File.Exists(selected))
                    throw new FileNotFoundException($"Explicit {description} path does not exist.", selected);
                var paths = EnumerateResourceFiles(selected, description == "skill" ? "skills" : "prompts").ToArray();
                if (paths.Length == 0 && File.Exists(selected))
                    throw new InvalidDataException(description == "skill"
                        ? "Explicit skill file must be named SKILL.md."
                        : "Explicit prompt template must be a .md file.");
                foreach (var path in paths)
                {
                    if (description == "skill" && !Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Explicit skill file must be named SKILL.md.");
                    var source = new ResourceSourceInfo(Path.GetFullPath(path), "local", "temporary", "top-level",
                        Path.GetDirectoryName(Path.GetFullPath(path)));
                    AddPath(target, seen, path, source);
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
                if (match) result.Add(Path.GetFullPath(entry));
                if (result.Count == 1000) break;
            }
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public string SystemInstructions()
    {
        var visible = Skills.Where(skill => !skill.ExplicitOnly).ToArray();
        return visible.Length == 0 ? "" : "Available skills (read SKILL.md when relevant; paths are absolute):\n" +
            string.Join('\n', visible.Select(skill => $"- {skill.Name}: {skill.Description} ({skill.Path})"));
    }

    public async Task<string> InvokeSkillAsync(string name, string arguments, CancellationToken cancellationToken = default)
    {
        var skill = Skills.SingleOrDefault(item => item.Name == name) ?? throw new ArgumentException($"Unknown skill: {name}");
        var (_, body) = Parse(await ReadBoundedAsync(skill.Path, cancellationToken));
        return $"<skill name=\"{skill.Name}\" path=\"{skill.Path}\">\n{body}\n</skill>\n\n{arguments}";
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
        var firstLine = body.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
        return firstLine.Length > 60 ? firstLine[..60] + "..." : firstLine;
    }

    private static (Dictionary<string, string> Metadata, string Body) Parse(string text)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\uFEFF');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return (metadata, normalized);
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) return (metadata, normalized);
        var frontmatterLines = normalized[4..end].Split('\n');
        for (var index = 0; index < frontmatterLines.Length; index++)
        {
            var line = frontmatterLines[index];
            var indentation = line.Length - line.TrimStart().Length;
            if (indentation > 0) continue;
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length > 0 && value[0] is '>' or '|')
                metadata[key] = ParseBlockScalar(value, indentation, frontmatterLines, ref index);
            else metadata[key] = value.Trim('"', '\'');
        }
        return (metadata, normalized[(end + 5)..]);
    }

    private static string ParseBlockScalar(string header, int keyIndentation, string[] lines, ref int index)
    {
        var content = new List<string>();
        var contentIndentation = -1;
        for (var next = index + 1; next < lines.Length; next++)
        {
            var line = lines[next];
            if (string.IsNullOrWhiteSpace(line))
            {
                content.Add("");
                index = next;
                continue;
            }

            var indentation = line.Length - line.TrimStart().Length;
            if (indentation <= keyIndentation) break;
            contentIndentation = contentIndentation < 0 ? indentation : contentIndentation;
            if (indentation < contentIndentation) break;
            content.Add(line[contentIndentation..]);
            index = next;
        }

        var trailingBreaks = 0;
        while (content.Count > 0 && content[^1].Length == 0)
        {
            trailingBreaks++;
            content.RemoveAt(content.Count - 1);
        }

        var value = header[0] == '|'
            ? string.Join('\n', content)
            : FoldBlockScalar(content);
        var chomp = header.Contains('+') ? '+' : header.Contains('-') ? '-' : ' ';
        var lineBreaks = chomp == '+' ? trailingBreaks + 1 : chomp == '-' ? 0 : 1;
        return value + new string('\n', lineBreaks);
    }

    private static string FoldBlockScalar(IReadOnlyList<string> lines)
    {
        var value = new StringBuilder();
        var priorLineWasBlank = false;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                value.Append('\n');
                priorLineWasBlank = true;
                continue;
            }
            if (value.Length > 0 && !priorLineWasBlank) value.Append(' ');
            value.Append(line);
            priorLineWasBlank = false;
        }
        return value.ToString();
    }

    private static async Task<string> ReadBoundedAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
        const int maxBytes = 64 * 1024;
        if (stream.Length > maxBytes) throw new InvalidDataException($"Resource exceeds 64KB: {path}");
        var buffer = new byte[maxBytes + 1];
        var count = 0;
        int read;
        while (count < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(count), token)) > 0)
            count += read;
        if (count > maxBytes) throw new InvalidDataException($"Resource exceeds 64KB: {path}");
        return new UTF8Encoding(false, true).GetString(buffer.AsSpan(0, count));
    }
}
