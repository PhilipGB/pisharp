using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Resources;

public sealed record SkillResource(string Name, string Description, string Path, bool ExplicitOnly);
public sealed record PromptResource(string Name, string Description, string Template, string Path);

/// <summary>Discovers bounded user resources; project auto-discovery requires trust, while explicit paths are caller-selected.</summary>
public sealed class ResourceCatalog
{
    public IReadOnlyList<SkillResource> Skills { get; }
    public IReadOnlyList<PromptResource> Prompts { get; }

    private ResourceCatalog(List<SkillResource> skills, List<PromptResource> prompts) => (Skills, Prompts) = (skills, prompts);

    public static async Task<ResourceCatalog> LoadAsync(string cwd, string agentDirectory, bool trusted,
        CancellationToken cancellationToken = default, bool discoverSkills = true, bool discoverPrompts = true,
        IReadOnlyList<string>? additionalSkills = null, IReadOnlyList<string>? additionalPrompts = null)
    {
        var skills = new List<SkillResource>();
        var prompts = new List<PromptResource>();
        var skillRoots = new List<string>
        {
            Path.Combine(agentDirectory, "skills"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills")
        };
        var promptRoots = new List<string> { Path.Combine(agentDirectory, "prompts") };
        if (trusted)
        {
            skillRoots.Add(Path.Combine(cwd, ".pi", "skills"));
            skillRoots.Add(Path.Combine(cwd, ".agents", "skills"));
            promptRoots.Add(Path.Combine(cwd, ".pi", "prompts"));
        }
        foreach (var root in (additionalSkills ?? []).Concat(discoverSkills ? skillRoots : []).Distinct(StringComparer.Ordinal))
        {
            var selected = Path.GetFullPath(root, cwd);
            if (!Directory.Exists(selected) && !File.Exists(selected))
            {
                if ((additionalSkills ?? []).Contains(root)) throw new FileNotFoundException("Explicit skill path does not exist.", selected);
                continue;
            }
            IEnumerable<string> paths = File.Exists(selected) ? [selected] : Directory.EnumerateFiles(selected, "SKILL.md", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).Take(1000);
            foreach (var path in paths)
            {
                if (!Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Explicit skill file must be named SKILL.md.");
                cancellationToken.ThrowIfCancellationRequested();
                var (metadata, _) = Parse(await ReadBoundedAsync(path, cancellationToken));
                if (!metadata.TryGetValue("name", out var name) ||
                    !Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant) || name.Length > 64 ||
                    !metadata.TryGetValue("description", out var description) || description.Length is 0 or > 1024 ||
                    skills.Any(item => item.Name == name)) continue;
                skills.Add(new(name, description, path, metadata.GetValueOrDefault("disable-model-invocation") == "true"));
            }
        }
        foreach (var root in (additionalPrompts ?? []).Concat(discoverPrompts ? promptRoots : []).Distinct(StringComparer.Ordinal))
        {
            var selected = Path.GetFullPath(root, cwd);
            if (!Directory.Exists(selected) && !File.Exists(selected))
            {
                if ((additionalPrompts ?? []).Contains(root)) throw new FileNotFoundException("Explicit prompt template path does not exist.", selected);
                continue;
            }
            IEnumerable<string> paths = File.Exists(selected) ? [selected] : Directory.EnumerateFiles(selected, "*.md", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal).Take(1000);
            foreach (var path in paths)
            {
                if (!Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Explicit prompt template must be a .md file.");
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(path);
                if (!Regex.IsMatch(name, "^[a-zA-Z0-9_-]+$", RegexOptions.CultureInvariant) ||
                    prompts.Any(item => item.Name == name)) continue;
                var (metadata, body) = Parse(await ReadBoundedAsync(path, cancellationToken));
                prompts.Add(new(name, metadata.GetValueOrDefault("description") ??
                    body.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "", body, path));
            }
        }
        return new(skills, prompts);
    }

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

    private static (Dictionary<string, string> Metadata, string Body) Parse(string text)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\uFEFF');
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return (metadata, normalized);
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) return (metadata, normalized);
        foreach (var line in normalized[4..end].Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator > 0) metadata[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"', '\'');
        }
        return (metadata, normalized[(end + 5)..]);
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
