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
        var selection = ResourcePathSelection.Create(cwd, agentDirectory, trusted, cancellationToken,
            discoverSkills, discoverPrompts, additionalSkills, additionalPrompts, userSkills, projectSkills,
            userPrompts, projectPrompts, extensionResources);
        var skills = new List<SkillResource>();
        var prompts = new List<PromptResource>();
        var skillDiagnostics = new List<ResourceDiagnostic>();
        var promptDiagnostics = new List<ResourceDiagnostic>();
        var skillCollisions = new List<ResourceDiagnostic>();
        var promptCollisions = new List<ResourceDiagnostic>();
        var skillFiles = selection.SkillFiles;
        var promptFiles = selection.PromptFiles;
        var loadedSkills = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var file in skillFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Diagnostic is { } diagnostic) { skillDiagnostics.Add(diagnostic); continue; }
            var path = file.Path;
            var source = file.Source;
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
            var identity = ResourcePathIdentity.Canonicalize(path);
            if (loadedSkills.Contains(identity)) continue;
            var existing = skills.FirstOrDefault(item => item.Name == name);
            if (existing is not null)
            {
                skillCollisions.Add(new("collision", $"name \"{name}\" collision", path,
                    new("skill", name, existing.Path, path, existing.SourceInfo, source), source));
                continue;
            }
            skills.Add(new(name, description, path, metadata.DisableModelInvocation, source));
            loadedSkills.Add(identity);
        }

        foreach (var file in promptFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = file.Path;
            var source = file.Source;
            var name = Path.GetFileNameWithoutExtension(path);
            var text = await ReadResourceAsync(path, source, promptDiagnostics, cancellationToken);
            if (text is null) continue;
            var metadata = ParseResource(text, path, source, promptDiagnostics, reportErrors: true);
            if (metadata is null) continue;
            var existing = prompts.FirstOrDefault(item => item.Name == name);
            if (existing is not null)
            {
                promptCollisions.Add(new("collision", $"name \"/{name}\" collision", path,
                    new("prompt", name, existing.Path, path, existing.SourceInfo, source), source));
                continue;
            }
            var body = metadata.Body;
            var description = metadata.Description;
            if (string.IsNullOrEmpty(description)) description = PromptDescription(body);
            prompts.Add(new(name, description, body, path, source));
        }

        skillDiagnostics.AddRange(skillCollisions);
        promptDiagnostics.AddRange(promptCollisions);
        promptDiagnostics.AddRange(selection.PromptPathErrors);
        return new(skills, prompts, skillDiagnostics, promptDiagnostics);
    }

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
        var trimmedArguments = ResourceFrontmatter.TrimBody(arguments);
        return trimmedArguments.Length == 0 ? skillBlock : $"{skillBlock}\n\n{trimmedArguments}";
    }

    public async Task<string> ResolveInputAsync(string input, CancellationToken cancellationToken = default,
        Func<ResourceExpansionError, Task>? reportError = null)
    {
        if (!input.StartsWith('/')) return input;
        if (input.StartsWith("/skill:", StringComparison.Ordinal))
        {
            var space = input.IndexOf(' ');
            var name = space < 0 ? input[7..] : input[7..space];
            var skill = Skills.FirstOrDefault(item => item.Name == name);
            if (skill is not null)
            {
                try { return await InvokeSkillAsync(name, space < 0 ? "" : input[(space + 1)..], cancellationToken); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or YamlException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reportError is not null)
                    {
                        var message = error switch
                        {
                            FileNotFoundException or DirectoryNotFoundException => $"ENOENT: no such file or directory, open '{skill.Path}'",
                            UnauthorizedAccessException => $"EACCES: permission denied, open '{skill.Path}'",
                            _ => error.Message
                        };
                        await reportError(new(skill.Path, "skill_expansion", message));
                    }
                }
            }
        }
        var separator = -1;
        for (var index = 1; index < input.Length; index++)
            if (ResourceFrontmatter.IsWhitespace(input[index])) { separator = index; break; }
        var command = separator < 0 ? input : input[..separator];
        var arguments = separator < 0 ? "" : input[(separator + 1)..];
        return Prompts.Any(prompt => "/" + prompt.Name == command) ? ExpandPrompt(command[1..], arguments) : input;
    }

    public string ExpandPrompt(string name, string arguments)
    {
        var prompt = Prompts.SingleOrDefault(item => item.Name == name) ?? throw new ArgumentException($"Unknown template: {name}");
        return PromptTemplateExpansion.Expand(prompt.Template, arguments);
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
