using System.Text.RegularExpressions;

namespace PiSharp.Core;

/// <summary>A markdown prompt template available as a slash command.</summary>
public sealed record PromptTemplate(
    string Name,
    string Description,
    string Content,
    string FilePath,
    string? ArgumentHint);

/// <summary>Loads and expands Pi-compatible markdown prompt templates.</summary>
public sealed class PromptTemplateCatalog
{
    private const string TemplateDirectoryName = "prompts";
    private static readonly Regex Placeholder = new(
        @"\$\{(?<defaultTarget>\d+|ARGUMENTS|@):-(?<default>[^}]*)\}|\$\{@:(?<sliceStart>\d+)(?::(?<sliceLength>\d+))?\}|\$(?<simple>ARGUMENTS|@|\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Loads user, project, and optional explicit template directories.</summary>
    public IReadOnlyList<PromptTemplate> Discover(
        string workspaceRoot,
        string? homeDirectory = null,
        IEnumerable<string>? explicitPaths = null,
        bool includeDefaults = true)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directories = includeDefaults
            ? new List<string>
            {
                Path.Combine(home, ".pi", "agent", TemplateDirectoryName),
                Path.Combine(root, ".pi", TemplateDirectoryName),
            }
            : [];
        directories.AddRange((explicitPaths ?? [])
            .Select(path => Path.IsPathRooted(path) ? path : Path.Combine(root, path)));
        return directories.SelectMany(LoadPath).ToArray();
    }

    /// <summary>Expands a slash command when a matching template exists.</summary>
    public static string Expand(string text, IReadOnlyList<PromptTemplate> templates)
    {
        if (!text.StartsWith('/') || text.Length == 1)
        {
            return text;
        }

        var separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
        var name = separator < 0 ? text[1..] : text[1..separator];
        var arguments = separator < 0 ? string.Empty : text[(separator + 1)..];
        var template = templates.FirstOrDefault(candidate => candidate.Name == name);
        return template is null ? text : Substitute(template.Content, ParseArguments(arguments));
    }

    /// <summary>Parses quoted and unquoted slash-command arguments.</summary>
    public static IReadOnlyList<string> ParseArguments(string text)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var character in text)
        {
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(character);
                }
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                AddArgument(arguments, current);
            }
            else
            {
                current.Append(character);
            }
        }

        AddArgument(arguments, current);
        return arguments;
    }

    /// <summary>Substitutes positional, all-argument, default, and slice placeholders.</summary>
    public static string Substitute(string content, IReadOnlyList<string> arguments)
    {
        var allArguments = string.Join(' ', arguments);
        return Placeholder.Replace(content, match => ResolvePlaceholder(match, arguments, allArguments));
    }

    private static string ResolvePlaceholder(Match match, IReadOnlyList<string> arguments, string allArguments)
    {
        var defaultTarget = match.Groups["defaultTarget"];
        if (defaultTarget.Success)
        {
            var value = ResolveTarget(defaultTarget.Value, arguments, allArguments);
            return string.IsNullOrEmpty(value) ? match.Groups["default"].Value : value;
        }

        var sliceStart = match.Groups["sliceStart"];
        if (sliceStart.Success)
        {
            var start = Math.Max(0, int.Parse(sliceStart.Value) - 1);
            var lengthGroup = match.Groups["sliceLength"];
            var length = lengthGroup.Success ? int.Parse(lengthGroup.Value) : arguments.Count;
            return string.Join(' ', arguments.Skip(start).Take(length));
        }

        return ResolveTarget(match.Groups["simple"].Value, arguments, allArguments);
    }

    private static string ResolveTarget(string target, IReadOnlyList<string> arguments, string allArguments)
    {
        if (target is "@" or "ARGUMENTS")
        {
            return allArguments;
        }
        return int.TryParse(target, out var number) && number > 0 && number <= arguments.Count
            ? arguments[number - 1]
            : string.Empty;
    }

    private static IEnumerable<PromptTemplate> LoadPath(string path)
    {
        if (File.Exists(path) && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var template = ReadTemplate(path);
            if (template is not null)
            {
                yield return template;
            }
            yield break;
        }
        foreach (var template in LoadDirectory(path))
        {
            yield return template;
        }
    }

    private static IEnumerable<PromptTemplate> LoadDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(path, "*.md", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.Ordinal);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            var template = ReadTemplate(file);
            if (template is not null)
            {
                yield return template;
            }
        }
    }

    private static PromptTemplate? ReadTemplate(string path)
    {
        try
        {
            var content = File.ReadAllText(path).Replace("\r\n", "\n");
            var body = StripFrontmatter(content, out var metadata);
            var firstLine = body.Split('\n').FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
            var description = metadata.GetValueOrDefault("description") ??
                              (firstLine.Length > 60 ? firstLine[..60] + "..." : firstLine);
            return new PromptTemplate(
                Path.GetFileNameWithoutExtension(path),
                description,
                body,
                Path.GetFullPath(path),
                metadata.GetValueOrDefault("argument-hint"));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string StripFrontmatter(string content, out Dictionary<string, string> metadata)
    {
        metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            return content;
        }

        var end = content.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return content;
        }

        foreach (var line in content[4..end].Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }
            metadata[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"', '\'');
        }

        return content[(end + "\n---".Length)..].TrimStart('\n');
    }

    private static void AddArgument(ICollection<string> arguments, System.Text.StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }
        arguments.Add(current.ToString());
        current.Clear();
    }
}
