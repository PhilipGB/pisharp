using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Extensions;

using var input = JsonDocument.Parse(File.ReadAllText(args[0]));
var results = new List<object>();
foreach (var scenario in input.RootElement.EnumerateArray())
{
    var id = scenario.GetProperty("id").GetString()!;
    var root = Path.Combine(args[1], id);
    var cwd = Path.Combine(root, scenario.TryGetProperty("cwd", out var directory) ? directory.GetString()! : "project");
    var agent = Path.Combine(root, "agent");
    var discover = scenario.TryGetProperty("discover", out var defaults) && defaults.GetBoolean();
    var trusted = scenario.TryGetProperty("trusted", out var trust) && trust.GetBoolean();
    var userSettings = await UserSettings.LoadAsync(agent, _ => null);
    var projectSettings = await UserSettings.LoadProjectAsync(cwd);
    if (scenario.TryGetProperty("includeThemes", out var includeThemes) && includeThemes.GetBoolean())
    {
        var catalogType = typeof(UserSettings).Assembly.GetType("PiSharp.Cli.Tui.TerminalThemeCatalog", throwOnError: true)!;
        var constructor = AssertSingleConstructor(catalogType);
        var arguments = constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "agentDirectory" => (object)agent,
            "trustedProjectDirectory" => trusted ? cwd : null,
            "trueColorOverride" => true,
            "explicitThemeBaseDirectory" => cwd,
            "discoverThemes" => discover,
            "userThemePaths" => userSettings.Themes,
            "projectThemePaths" => trusted ? projectSettings?.Themes : null,
            "userPackages" => userSettings.Packages,
            "projectPackages" => trusted ? projectSettings?.Packages : null,
            _ => parameter.DefaultValue
        }).ToArray();
        var themes = constructor.Invoke(arguments);
        var names = (IReadOnlyList<string>)catalogType.GetMethod("GetAvailableNames")!.Invoke(themes, null)!;
        results.Add(new { id, themes = names.Where(name => name is not ("dark" or "light" or "system"))
            .Order(StringComparer.Ordinal).Select(name =>
            {
                var theme = catalogType.GetMethod("Load")!.Invoke(themes, [name])!;
                var accent = (string)theme.GetType().GetMethod("Fg",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(theme, ["accent"])!;
                return new { name, accent = accent + "sample\u001b[39m" };
            }).ToArray() });
        continue;
    }
    ResourceCatalog catalog;
    try
    {
        catalog = await ResourceCatalog.LoadAsync(cwd, agent, trusted,
            discoverSkills: discover, discoverPrompts: discover,
            additionalSkills: Paths("skillPaths"), additionalPrompts: Paths("promptPaths"),
            userSkills: Settings("user", "skills"), userPrompts: Settings("user", "prompts"),
            projectSkills: Settings("project", "skills"), projectPrompts: Settings("project", "prompts"),
            extensionResources: new ExtensionResourceDiscovery(ExtensionPaths("extensionSkillPaths"),
                ExtensionPaths("extensionPromptPaths"), [], []),
            userPackages: userSettings.Packages, projectPackages: projectSettings?.Packages);
    }
    catch (Exception error) when (scenario.TryGetProperty("expectPathError", out var expectedError) && expectedError.GetBoolean())
    {
        results.Add(new { id, pathError = error.Message });
        continue;
    }
    var skills = new List<object>();
    foreach (var skill in catalog.Skills)
    {
        var expanded = await catalog.InvokeSkillAsync(skill.Name, "");
        var body = expanded[(expanded.IndexOf("\n\n", StringComparison.Ordinal) + 2)..^9];
        skills.Add(new { name = skill.Name, description = skill.Description, path = Relative(skill.Path),
            body = Summary(body), sourceInfo = Source(skill.SourceInfo) });
    }
    results.Add(new { id, skills, prompts = catalog.Prompts.Select(p => new { name = p.Name, description = p.Description,
        path = Relative(p.Path), body = Summary(p.Template), sourceInfo = Source(p.SourceInfo) }).ToArray(),
        skillDiagnostics = catalog.SkillDiagnostics.Select(Diagnostic).ToArray(),
        promptDiagnostics = catalog.PromptDiagnostics.Select(Diagnostic).ToArray() });

    string? Relative(string? path) => path is null ? null : Path.GetRelativePath(root, path);
    string[] Paths(string property) => scenario.TryGetProperty(property, out var paths)
        ? paths.EnumerateArray().Select(p => Path.Combine(root, p.GetString()!)).ToArray() : [];
    string[]? Settings(string scope, string property) => scenario.TryGetProperty(scope, out var settings) &&
        settings.TryGetProperty(property, out var values) ? values.EnumerateArray().Select(v => v.GetString()!).ToArray() : null;
    ExtensionDiscoveredResourcePath[] ExtensionPaths(string property) => Paths(property).Select(path =>
        new ExtensionDiscoveredResourcePath(path, new ResourceSourceInfo(path, "fixture-extension", "user", "package",
            Path.Combine(root, "outside")))).ToArray();
    object Source(ResourceSourceInfo source) => new { path = Relative(source.Path), source = source.Source,
        scope = source.Scope, origin = source.Origin, baseDir = Relative(source.BaseDir) };
    object Diagnostic(ResourceDiagnostic diagnostic) => new { type = diagnostic.Type, message = diagnostic.Message,
        path = Relative(diagnostic.Path), collision = diagnostic.Collision is { } collision
            ? new { resourceType = collision.ResourceType, name = collision.Name,
                winnerPath = Relative(collision.WinnerPath), loserPath = Relative(collision.LoserPath) } : null };
}
Console.WriteLine(JsonSerializer.Serialize(results));
static object Summary(string text) => new { hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), characters = text.Length };
static System.Reflection.ConstructorInfo AssertSingleConstructor(Type type) => type.GetConstructors().Single();
