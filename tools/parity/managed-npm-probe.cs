using System.Text.Json;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

var root = Path.GetFullPath(args[0]);
var agent = Path.GetFullPath(args[1]);
var cwd = Path.GetFullPath(args[2]);
var source = args[3];
Directory.CreateDirectory(agent);
Directory.CreateDirectory(cwd);

var package = new ResourcePackageSource(source);
var store = new ManagedResourcePackageStore(agent);
var roots = await store.ResolveAsync(cwd, trusted: false, userPackages: [package]);
var packageRoot = roots.Single(root => root.Scope == "user" && root.Source == source).Path;
var resourcePasses = new List<object>();
var extensionPasses = new List<object>();
foreach (var (phase, reason) in new[]
         {
             ("startup", ExtensionResourceDiscoveryReason.Startup),
             ("reloaded", ExtensionResourceDiscoveryReason.Reload)
         })
{
    var discoveredSkills = Path.Combine(cwd, ".pi", "extension-resources", phase, "skills", phase);
    Directory.CreateDirectory(discoveredSkills);
    await File.WriteAllTextAsync(Path.Combine(discoveredSkills, "SKILL.md"),
        $"---\nname: managed-{phase}-extension-skill\ndescription: Extension resource {phase}\n---\nLoaded during {phase}.");

    using var extensions = ExtensionCatalog.Load(agent, cwd, projectTrusted: false,
        userPackages: [package], managedPackageRoots: roots);
    if (extensions.LoadErrors.Count > 0)
        throw new InvalidOperationException(string.Join("\n", extensions.LoadErrors.Select(error => error.Error)));
    extensionPasses.Add(extensions.LoadedExtensions.Select(extension => new
    {
        path = RelativeToPackage(extension.Path),
        source = extension.Source,
        scope = extension.Scope,
        origin = extension.Origin,
        baseDir = RelativeBase(extension.BaseDir)
    }).ToArray());
    var discovered = await extensions.DiscoverResourcesAsync(cwd, reason);
    var resources = await ResourceCatalog.LoadAsync(cwd, agent, trusted: false,
        extensionResources: discovered, userPackages: [package], managedPackageRoots: roots);
    resourcePasses.Add(new
    {
        skills = resources.Skills.Select(skill => new
        {
            name = skill.Name,
            path = RelativeToPackage(skill.Path),
            enabled = true,
            source = skill.SourceInfo.Source,
            scope = skill.SourceInfo.Scope,
            origin = skill.SourceInfo.Origin,
            baseDir = RelativeBase(skill.SourceInfo.BaseDir)
        }).OrderBy(skill => skill.name, StringComparer.Ordinal).ToArray(),
        prompts = resources.Prompts.Select(prompt => new
        {
            name = prompt.Name,
            path = RelativeToPackage(prompt.Path),
            enabled = true,
            source = prompt.SourceInfo.Source,
            scope = prompt.SourceInfo.Scope,
            origin = prompt.SourceInfo.Origin,
            baseDir = RelativeBase(prompt.SourceInfo.BaseDir)
        }).OrderBy(prompt => prompt.name, StringComparer.Ordinal).ToArray()
    });
}

var themePath = Path.Combine(packageRoot, "themes", "managed.json");
if (!File.Exists(themePath)) throw new FileNotFoundException("Managed theme was not installed.", themePath);
var dependencyInstalled = Directory.Exists(Path.Combine(agent, "npm", "node_modules", "managed-parity-dependency"));
var peerInstalled = Directory.Exists(Path.Combine(agent, "npm", "node_modules", "pisharp-host-api-must-not-install"));
Console.WriteLine(JsonSerializer.Serialize(new
{
    roots = roots.Select(root => new { source = root.Source, scope = root.Scope, path = RelativeToPackage(root.Path) }),
    resourcePasses,
    extensionPasses,
    theme = new { path = RelativeToPackage(themePath), exists = true },
    dependencyInstalled,
    peerInstalled
}));

string RelativeToPackage(string path) => Path.GetRelativePath(packageRoot, path).Replace('\\', '/');
string? RelativeBase(string? path) => path is null ? null : RelativeToPackage(path);
