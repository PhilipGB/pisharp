using System.Text.Json;
using PiSharp.ResourceDiscoveryExtension;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

var results = new List<object>();
foreach (var id in new[] { "disabled", "enabled", "untrusted" })
{
    var root = Path.Combine(args[0], id);
    var cwd = Path.Combine(root, "project");
    var agent = Path.Combine(root, "agent");
    var package = Path.Combine(root, "package");
    var file = Path.Combine(package, "extensions", "index.dll");
    var trace = Path.Combine(root, "native-trace.log");
    Directory.CreateDirectory(Path.Combine(cwd, ".pi"));
    Directory.CreateDirectory(agent);
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, file);
    File.WriteAllText(Path.Combine(package, "package.json"), "{\"pi\":{\"extensions\":[\"extensions\"]}}");
    var previous = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG");
    try
    {
        Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", trace);
        using var bootstrap = ExtensionCatalog.Load(agent, cwd, false, userPackages: [new("../package")]);
        var bootstrapCount = bootstrap.LoadedExtensions.Count;
        using var catalog = ExtensionCatalog.Load(agent, cwd, id != "untrusted", preloaded: bootstrap,
            userPackages: [new("../package")], projectPackages: [new("../../package",
                Extensions: id == "enabled" ? null : [], HasFilter: id != "enabled")]);
        string? Relative(string? path) => path is null ? null : Path.GetRelativePath(root, path).Replace(".dll", ".<module>", StringComparison.Ordinal);
        results.Add(new { id, bootstrapCount, configurationTrace = File.ReadAllLines(trace), errors = catalog.LoadErrors,
            loaded = catalog.LoadedExtensions.Select(extension => new
            {
                path = Relative(extension.Path),
                sourceInfo = new { path = Relative(extension.Path), source = extension.Source, scope = extension.Scope,
                    origin = extension.Origin, baseDir = Relative(extension.BaseDir) }
            }).ToArray() });
    }
    finally { Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", previous); }
}
Console.WriteLine(JsonSerializer.Serialize(results));
