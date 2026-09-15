using System.Text;
using System.Xml;

namespace PiSharp.Core;

/// <summary>A discoverable Agent Skill descriptor.</summary>
public sealed record SkillDefinition(
    string Name,
    string Description,
    string FilePath,
    string BaseDirectory,
    bool DisableModelInvocation);

/// <summary>A non-fatal issue found while discovering a skill.</summary>
public sealed record SkillDiagnostic(string Message, string Path);

/// <summary>Results from deterministic skill discovery.</summary>
public sealed record SkillDiscoveryResult(
    IReadOnlyList<SkillDefinition> Skills,
    IReadOnlyList<SkillDiagnostic> Diagnostics);

/// <summary>
/// Discovers Agent Skills from the user and workspace .pi directories and
/// formats their metadata for the system prompt.
/// </summary>
public sealed class SkillCatalog
{
    private const int MaximumNameLength = 64;
    private const int MaximumDescriptionLength = 1024;
    private const string SkillFileName = "SKILL.md";

    /// <summary>Discovers skills with user definitions taking precedence over project collisions.</summary>
    public SkillDiscoveryResult Discover(
        string workspaceRoot,
        string? homeDirectory = null,
        IEnumerable<string>? additionalPaths = null,
        bool includeDefaults = true,
        bool includeProjectDefaults = true)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var home = homeDirectory ?? Environment.GetEnvironmentVariable("HOME") ??
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var skills = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);
        var diagnostics = new List<SkillDiagnostic>();
        if (includeDefaults)
        {
            AddDirectory(Path.Combine(home, ".pi", "agent", "skills"), skills, diagnostics);
            AddDirectory(Path.Combine(home, ".agents", "skills"), skills, diagnostics);
            if (includeProjectDefaults)
            {
                AddDirectory(Path.Combine(root, ".pi", "skills"), skills, diagnostics);
                AddAncestorAgentSkills(root, home, skills, diagnostics);
            }
        }
        foreach (var path in additionalPaths ?? [])
        {
            var resolvedPath = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
            AddPath(resolvedPath, skills, diagnostics);
        }
        return new SkillDiscoveryResult(skills.Values.ToArray(), diagnostics);
    }

    /// <summary>Formats model-invocable skills as the XML block used by Pi.</summary>
    public static string FormatForPrompt(IEnumerable<SkillDefinition> skills)
    {
        var visible = skills.Where(skill => !skill.DisableModelInvocation).ToArray();
        if (visible.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("The following skills provide specialized instructions for specific tasks.");
        builder.AppendLine("Use the read tool to load a skill's file when the task matches its description.");
        builder.AppendLine("When a skill file references a relative path, resolve it against the skill directory (parent of SKILL.md / dirname of the path) and use that absolute path in tool commands.");
        builder.AppendLine();
        builder.AppendLine("<available_skills>");
        foreach (var skill in visible)
        {
            builder.AppendLine("  <skill>");
            builder.AppendLine($"    <name>{EscapeXml(skill.Name)}</name>");
            builder.AppendLine($"    <description>{EscapeXml(skill.Description)}</description>");
            builder.AppendLine($"    <location>{EscapeXml(skill.FilePath)}</location>");
            builder.AppendLine("  </skill>");
        }

        builder.Append("</available_skills>");
        return builder.ToString();
    }

    /// <summary>Expands an explicit /skill:name command into a self-contained skill block.</summary>
    public static string ExpandCommand(string text, IReadOnlyList<SkillDefinition> skills)
    {
        const string SkillPrefix = "/skill:";
        if (!text.StartsWith(SkillPrefix, StringComparison.Ordinal))
        {
            return text;
        }

        var separator = text.IndexOf(' ');
        var name = separator < 0 ? text[SkillPrefix.Length..] : text[SkillPrefix.Length..separator];
        var arguments = separator < 0 ? string.Empty : text[(separator + 1)..].Trim();
        var skill = skills.FirstOrDefault(candidate => candidate.Name == name);
        if (skill is null)
        {
            return text;
        }

        try
        {
            var body = StripFrontmatter(File.ReadAllText(skill.FilePath)).Trim();
            var block = $"<skill name=\"{XmlConvert.EncodeName(skill.Name)}\" location=\"{skill.FilePath}\">\nReferences are relative to {skill.BaseDirectory}.\n\n{body}\n</skill>";
            return string.IsNullOrEmpty(arguments) ? block : $"{block}\n\n{arguments}";
        }
        catch (IOException)
        {
            return text;
        }
        catch (UnauthorizedAccessException)
        {
            return text;
        }
    }

    private static void AddAncestorAgentSkills(
        string root,
        string home,
        IDictionary<string, SkillDefinition> skills,
        ICollection<SkillDiagnostic> diagnostics)
    {
        var globalPath = Path.GetFullPath(Path.Combine(home, ".agents", "skills"));
        var current = Path.GetFullPath(root);
        while (true)
        {
            var candidate = Path.GetFullPath(Path.Combine(current, ".agents", "skills"));
            if (!string.Equals(candidate, globalPath, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                AddDirectory(candidate, skills, diagnostics);
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null)
            {
                return;
            }
            current = parent;
        }
    }

    private static void AddPath(
        string path,
        IDictionary<string, SkillDefinition> skills,
        ICollection<SkillDiagnostic> diagnostics)
    {
        var resolvedPath = Path.GetFullPath(path);
        if (Directory.Exists(resolvedPath))
        {
            AddDirectory(resolvedPath, skills, diagnostics);
            return;
        }
        if (!File.Exists(resolvedPath))
        {
            diagnostics.Add(new SkillDiagnostic("skill path does not exist", resolvedPath));
            return;
        }
        if (!resolvedPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new SkillDiagnostic("skill path is not a markdown file", resolvedPath));
            return;
        }

        var result = ReadSkill(resolvedPath);
        foreach (var diagnostic in result.Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }
        if (result.Skill is not null && !skills.ContainsKey(result.Skill.Name))
        {
            skills.Add(result.Skill.Name, result.Skill);
        }
    }

    private static void AddDirectory(
        string directory,
        IDictionary<string, SkillDefinition> skills,
        ICollection<SkillDiagnostic> diagnostics)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var file in EnumerateSkillFiles(directory))
            {
                var result = ReadSkill(file);
                foreach (var diagnostic in result.Diagnostics)
                {
                    diagnostics.Add(diagnostic);
                }
                if (result.Skill is not null && !skills.ContainsKey(result.Skill.Name))
                {
                    skills.Add(result.Skill.Name, result.Skill);
                }
            }
        }
        catch (IOException exception)
        {
            diagnostics.Add(new SkillDiagnostic(exception.Message, directory));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new SkillDiagnostic(exception.Message, directory));
        }
    }

    private static IEnumerable<string> EnumerateSkillFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var declaredSkill = Path.Combine(directory, SkillFileName);
            if (File.Exists(declaredSkill))
            {
                yield return declaredSkill;
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.md")
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(directory)
                         .Where(path => !Path.GetFileName(path).StartsWith('.') &&
                                        !string.Equals(Path.GetFileName(path), "node_modules", StringComparison.Ordinal))
                         .OrderByDescending(path => path, StringComparer.Ordinal))
            {
                pending.Push(child);
            }
        }
    }

    private static (SkillDefinition? Skill, IReadOnlyList<SkillDiagnostic> Diagnostics) ReadSkill(string filePath)
    {
        var diagnostics = new List<SkillDiagnostic>();
        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (IOException exception)
        {
            return (null, [new SkillDiagnostic(exception.Message, filePath)]);
        }
        catch (UnauthorizedAccessException exception)
        {
            return (null, [new SkillDiagnostic(exception.Message, filePath)]);
        }

        var frontmatter = ParseFrontmatter(content);
        var declaredName = frontmatter.GetValueOrDefault("name");
        var name = string.IsNullOrWhiteSpace(declaredName)
            ? Path.GetFileName(Path.GetDirectoryName(filePath) ?? string.Empty)
            : declaredName;
        var description = frontmatter.GetValueOrDefault("description", string.Empty);
        Validate(name, description, filePath, diagnostics);
        if (string.IsNullOrWhiteSpace(description))
        {
            return (null, diagnostics);
        }

        var disabled = string.Equals(frontmatter.GetValueOrDefault("disable-model-invocation"), "true", StringComparison.OrdinalIgnoreCase);
        return (new SkillDefinition(name, description, filePath, Path.GetDirectoryName(filePath)!, disabled), diagnostics);
    }

    private static Dictionary<string, string> ParseFrontmatter(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return values;
        }

        for (var index = 1; index < lines.Length && lines[index].Trim() != "---"; index++)
        {
            var separator = lines[index].IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = lines[index][..separator].Trim();
            var value = lines[index][(separator + 1)..].Trim().Trim('"', '\'');
            values[key] = value;
        }

        return values;
    }

    private static void Validate(string name, string description, string path, ICollection<SkillDiagnostic> diagnostics)
    {
        if (name.Length > MaximumNameLength)
        {
            diagnostics.Add(new SkillDiagnostic($"name exceeds {MaximumNameLength} characters", path));
        }
        if (name.Any(character => !(char.IsLower(character) || char.IsDigit(character) || character == '-')) ||
            name.StartsWith('-') || name.EndsWith('-') || name.Contains("--", StringComparison.Ordinal))
        {
            diagnostics.Add(new SkillDiagnostic("name contains invalid characters or hyphen placement", path));
        }
        if (string.IsNullOrWhiteSpace(description))
        {
            diagnostics.Add(new SkillDiagnostic("description is required", path));
        }
        else if (description.Length > MaximumDescriptionLength)
        {
            diagnostics.Add(new SkillDiagnostic($"description exceeds {MaximumDescriptionLength} characters", path));
        }
    }

    private static string StripFrontmatter(string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return normalized;
        }

        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        return end < 0 ? normalized : normalized[(end + "\n---".Length)..];
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
}
