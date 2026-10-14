using System.Text.Json;
using PiSharp.ResourceDiscoveryExtension;
using PiSharp.Runtime.Extensions;

var results = new List<object>();
foreach (var id in new[] { "plain", "file-url", "alias" })
{
    var root = Path.Combine(args[0], id);
    var cwd = Path.Combine(root, "project");
    var agent = Path.Combine(root, "agent");
    var package = Path.Combine(root, "package");
    var file = Path.Combine(package, "extensions", "index.dll");
    Directory.CreateDirectory(cwd);
    Directory.CreateDirectory(agent);
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, file);
    File.WriteAllText(Path.Combine(package, "package.json"), "{\"pi\":{\"extensions\":[\"extensions\"]}}");
    var alias = Path.Combine(root, "alias.dll");
    File.CreateSymbolicLink(alias, file);
    var entry = id == "alias" ? alias : id == "file-url" ? new Uri(file).AbsoluteUri : file;
    using var catalog = ExtensionCatalog.Load(agent, cwd, false, additionalPaths: [entry], userPackages: [new("../package")]);
    string? Relative(string? path) => path is null ? null : Path.GetRelativePath(root, path).Replace(".dll", ".<module>", StringComparison.Ordinal);
    results.Add(new { id, errors = catalog.LoadErrors, loaded = catalog.LoadedExtensions.Select(extension => new
    {
        path = Relative(extension.Path),
        sourceInfo = new { path = Relative(extension.Path), source = extension.Source, scope = extension.Scope,
            origin = extension.Origin, baseDir = Relative(extension.BaseDir) }
    }).ToArray() });
}
Console.WriteLine(JsonSerializer.Serialize(results));
