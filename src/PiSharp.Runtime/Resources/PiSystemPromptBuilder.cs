using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Resources;

/// <summary>Builds Pi's structured system prompt from the current active tool and resource state.</summary>
internal static class PiSystemPromptBuilder
{
    private const string DefaultPreamble =
        "You are an expert coding assistant operating inside pi, a coding agent harness. You help users by reading files, executing commands, editing code, and writing new files.";

    private static readonly IReadOnlyDictionary<string, (string Snippet, string[] Guidelines)> s_builtinContributions =
        new Dictionary<string, (string, string[])>(StringComparer.Ordinal)
        {
            ["read"] = ("Read file contents", ["Use read to examine files instead of cat or sed."]),
            ["bash"] = ("Execute bash commands (ls, grep, find, etc.)",
                ["You can inspect PI_* environment variables for current model and session details."]),
            ["powershell"] = ("Execute PowerShell commands",
                ["You can inspect PI_* environment variables for current model and session details."]),
            ["edit"] = ("Make precise file edits with exact text replacement, including multiple disjoint edits in one call",
            [
                "Use edit for precise changes (edits[].oldText must match exactly)",
                "When changing multiple separate locations in one file, use one edit call with multiple entries in edits[] instead of multiple edit calls",
                "Each edits[].oldText is matched against the original file, not after earlier edits are applied. Do not emit overlapping or nested edits. Merge nearby changes into one edit.",
                "Keep edits[].oldText as small as possible while still being unique in the file. Do not pad with large unchanged regions."
            ]),
            ["write"] = ("Create or overwrite files", ["Use write only for new files or complete rewrites."]),
            ["grep"] = ("Search file contents for patterns (respects .gitignore)", []),
            ["find"] = ("Find files by glob pattern (respects .gitignore)", []),
            ["ls"] = ("List directory contents", [])
        };

    internal static string? BuiltinSnippet(string name) =>
        s_builtinContributions.TryGetValue(name, out var contribution) ? contribution.Snippet : null;

    internal static IReadOnlyList<string> BuiltinGuidelines(string name) =>
        s_builtinContributions.TryGetValue(name, out var contribution) ? contribution.Guidelines : [];

    internal static string Build(string workingDirectory, IReadOnlyList<PiSharpToolDeclaration> tools,
        string? customPrompt, string? appendSystemPrompt, string? projectInstructions, string? skillInstructions)
    {
        var sections = new List<string>
        {
            string.IsNullOrEmpty(customPrompt) ? DefaultPreamble : customPrompt
        };

        if (string.IsNullOrEmpty(customPrompt))
        {
            var snippets = tools.Where(tool => !string.IsNullOrWhiteSpace(tool.Registration.PromptSnippet))
                .Select(tool => $"- {tool.Registration.Function.Name}: {tool.Registration.PromptSnippet}").ToArray();
            var toolText = snippets.Length == 0 ? "(none)" : string.Join('\n', snippets);
            sections.Add($"<tools>\n{toolText}\n\nIn addition to the tools above, you may have access to other custom tools depending on the project.\n</tools>");
            sections.Add($"<rules>\n{BuildRules(tools)}\n</rules>");
            sections.Add($"<docs>\n{BuildDocumentationSection()}\n</docs>");
        }

        if (!string.IsNullOrEmpty(appendSystemPrompt))
            sections.Add($"<addendum>\n{appendSystemPrompt}\n</addendum>");

        if (!string.IsNullOrWhiteSpace(projectInstructions))
            sections.Add($"<project_context>\nProject-specific instructions and guidelines:\n\n{projectInstructions.TrimEnd()}\n</project_context>");

        if (!string.IsNullOrWhiteSpace(skillInstructions))
        {
            var skills = skillInstructions.Trim();
            sections.Add(skills.StartsWith("<skills>", StringComparison.Ordinal) &&
                         skills.EndsWith("</skills>", StringComparison.Ordinal)
                ? skills
                : $"<skills>\n{skills}\n</skills>");
        }

        sections.Add($"<cwd>\n{workingDirectory.Replace('\\', '/')}\n</cwd>");
        return string.Join("\n\n", sections);
    }

    private static string BuildRules(IReadOnlyList<PiSharpToolDeclaration> tools)
    {
        var names = tools.Select(tool => tool.Registration.Function.Name).ToHashSet(StringComparer.Ordinal);
        var rules = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? rule)
        {
            var normalized = rule?.Trim();
            if (!string.IsNullOrEmpty(normalized) && seen.Add(normalized)) rules.Add(normalized);
        }

        if ((names.Contains("bash") || names.Contains("powershell")) &&
            !names.Contains("grep") && !names.Contains("find") && !names.Contains("ls"))
        {
            Add(names.Contains("bash") && names.Contains("powershell")
                ? "Use bash or PowerShell for file operations like listing, searching, and finding files"
                : names.Contains("powershell")
                    ? "Use PowerShell for file operations like listing, searching, and finding files"
                    : "Use bash for file operations like ls, rg, find");
        }

        foreach (var tool in tools)
            foreach (var guideline in tool.Registration.PromptGuidelines ?? []) Add(guideline);

        Add("Be concise in your responses");
        Add("Show file paths clearly when working with files");
        return string.Join('\n', rules.Select(rule => "- " + rule));
    }

    private static string BuildDocumentationSection()
    {
        var paths = PiDocumentationPaths.Resolve();
        return $"""
Pi documentation (read only when the user asks about pi itself, its SDK, extensions, themes, skills, or TUI):
- Main documentation: {paths.Readme}
- Additional docs: {paths.Docs}
- Examples: {paths.Examples} (extensions, custom tools, SDK)
- When reading pi docs or examples, resolve docs/... under Additional docs and examples/... under Examples, not the current working directory
- When asked about: extensions (docs/extensions.md, examples/extensions/), themes (docs/themes.md), skills (docs/skills.md), prompt templates (docs/prompt-templates.md), TUI components (docs/tui.md), keybindings (docs/keybindings.md), SDK integrations (docs/sdk.md), custom providers (docs/custom-provider.md), adding models (docs/models.md), pi packages (docs/packages.md), environment variables (docs/environment-variables.md), MCP servers (docs/mcp.md), codemode scripts and non-LLM models such as classifiers and image models (docs/codemode.md)
- When working on pi topics, read the docs and examples, and follow .md cross-references before implementing
- Always read pi .md files completely and follow links to related docs (e.g., tui.md for TUI API details)
""";
    }

    private sealed record DocumentationPaths(string Readme, string Docs, string Examples);

    private static class PiDocumentationPaths
    {
        private static readonly Lazy<DocumentationPaths> s_paths = new(ResolveFromAssembly);

        internal static DocumentationPaths Resolve() => s_paths.Value;

        private static DocumentationPaths ResolveFromAssembly()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var readme = Path.Combine(directory.FullName, "README.md");
                var docs = Path.Combine(directory.FullName, "docs");
                if (File.Exists(readme) && Directory.Exists(docs))
                    return new(Path.GetFullPath(readme), Path.GetFullPath(docs), Path.GetFullPath(Path.Combine(directory.FullName, "examples")));
            }

            var fallback = Path.GetFullPath(AppContext.BaseDirectory);
            return new(Path.Combine(fallback, "README.md"), Path.Combine(fallback, "docs"), Path.Combine(fallback, "examples"));
        }
    }
}
