using System.Text.Json;
using PiSharp.Runtime.Resources;

var root = Path.GetFullPath(args[0]);
var scenario = args[1];
var agent = Path.Combine(root, "agent");
var cwd = Path.Combine(root, "project");
var package = scenario == "git"
    ? Path.Combine(agent, "git", "127.0.0.1", "fixture", "repo")
    : Path.Combine(root, "package");
Directory.CreateDirectory(agent);
Directory.CreateDirectory(Path.Combine(cwd, ".pi"));
WritePackage("reload-resource", "Initial managed package resource.");

var source = scenario == "git" ? "git:git://127.0.0.1:54321/fixture/repo.git@main" : package;
var trusted = true;
WriteSettings(userPackages: scenario == "trust-revoked" ? [] : [source],
    projectPackages: scenario == "trust-revoked" ? [new PackageEntry(source)] : []);
var before = await SnapshotAsync(trusted);

switch (scenario)
{
    case "local":
    case "git":
        break;
    case "updated-resource":
        WritePackage("reload-resource-v2", "Updated managed package resource.");
        break;
    case "removed-resource":
        WriteSettings([], []);
        Directory.Delete(package, recursive: true);
        break;
    case "disabled-resource":
        WriteSettings([source], [new PackageEntry(source, Skills: [])]);
        break;
    case "trust-revoked":
        trusted = false;
        break;
    default:
        throw new ArgumentException($"Unknown startup/reload scenario: {scenario}");
}

var after = await SnapshotAsync(trusted);
Console.WriteLine(JsonSerializer.Serialize(new { scenario, before, after }));

async Task<object> SnapshotAsync(bool isTrusted)
{
    var (userPackages, projectPackages) = ReadSettings(isTrusted);
    var roots = await new ManagedResourcePackageStore(agent).ResolveAsync(cwd, isTrusted,
        userPackages, projectPackages, offline: true);
    var catalog = await ResourceCatalog.LoadAsync(cwd, agent, isTrusted,
        discoverSkills: true, discoverPrompts: true,
        userPackages: userPackages, projectPackages: projectPackages, managedPackageRoots: roots);
    var selectedName = catalog.Skills.FirstOrDefault()?.Name ?? "reload-resource";
    var invocation = await catalog.ResolveInputAsync($"/skill:{selectedName} args");
    return new
    {
        skills = catalog.Skills.Select(skill => new { name = skill.Name, description = skill.Description })
            .OrderBy(skill => skill.name, StringComparer.Ordinal).ToArray(),
        prompts = catalog.Prompts.Select(prompt => prompt.Name).Order(StringComparer.Ordinal).ToArray(),
        invocation = invocation.Replace(root, "<workspace>", StringComparison.Ordinal)
    };
}

(IReadOnlyList<ResourcePackageSource> User, IReadOnlyList<ResourcePackageSource> Project) ReadSettings(bool isTrusted)
{
    var user = ReadPackageEntries(Path.Combine(agent, "settings.json"));
    var project = isTrusted ? ReadPackageEntries(Path.Combine(cwd, ".pi", "settings.json")) : [];
    return (user, project);
}

void WriteSettings(IReadOnlyList<string> userPackages, IReadOnlyList<PackageEntry> projectPackages)
{
    File.WriteAllText(Path.Combine(agent, "settings.json"), JsonSerializer.Serialize(new
    {
        packages = userPackages
    }));
    File.WriteAllText(Path.Combine(cwd, ".pi", "settings.json"), JsonSerializer.Serialize(new
    {
        packages = projectPackages.Select(entry => entry.Skills is null && entry.Autoload
            ? (object)entry.Source : entry.ToJson()).ToArray()
    }));
}

List<ResourcePackageSource> ReadPackageEntries(string path)
{
    if (!File.Exists(path)) return [];
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    if (!document.RootElement.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
        return [];
    var result = new List<ResourcePackageSource>();
    foreach (var item in packages.EnumerateArray())
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            result.Add(new(item.GetString()!));
            continue;
        }
        var packageSource = item.GetProperty("source").GetString()!;
        var autoload = !item.TryGetProperty("autoload", out var enabled) || enabled.ValueKind != JsonValueKind.False;
        var skills = item.TryGetProperty("skills", out var entries)
            ? entries.EnumerateArray().Select(entry => entry.GetString()!).ToArray()
            : null;
        result.Add(new(packageSource, Skills: skills, Autoload: autoload, HasFilter: true));
    }
    return result;
}

void WritePackage(string skillName, string body)
{
    var skillDirectory = Path.Combine(package, "skills", "guide");
    Directory.CreateDirectory(skillDirectory);
    File.WriteAllText(Path.Combine(package, "package.json"), "{\"pi\":{\"skills\":[\"skills\"]}}\n");
    File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"),
        $"---\nname: {skillName}\ndescription: {skillName}\n---\n{body}\n");
}

sealed record PackageEntry(string Source, bool Autoload = true, IReadOnlyList<string>? Skills = null)
{
    public object ToJson() => new { source = Source, autoload = Autoload, skills = Skills ?? [] };
}
