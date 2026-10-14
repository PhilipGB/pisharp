using System.Text.Json;
using PiSharp.Runtime.Extensions;
using PiSharp.ResourceDiscoveryExtension;

var root = args[0];
var agent = Path.Combine(root, "agent");
Directory.CreateDirectory(agent);
Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_PARTIAL_FAILURE", "true");
var bad = Path.Combine(root, "bad.dll");
var guide = Path.Combine(root, "guide.dll");
File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, bad);
File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location, guide);
using var catalog = ExtensionCatalog.Load(agent, root, false, discover: false, additionalPaths: [bad, guide]);
Console.WriteLine(JsonSerializer.Serialize(new
{
    loaded = catalog.LoadedExtensions.Select(extension => Path.GetFileNameWithoutExtension(extension.Path)).ToArray(),
    errors = catalog.LoadErrors.Select(error => new { module = Path.GetFileNameWithoutExtension(error.Path), error = error.Error }).ToArray(),
    partialCommandPresent = catalog.Registration.Commands.ContainsKey("partial_command"),
    partialToolPresent = catalog.Registration.Tools.Any(tool => tool.Name == "partial_tool")
}));
