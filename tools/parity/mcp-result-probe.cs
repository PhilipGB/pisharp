using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Tools;

var root = args[0];
using var scenarios = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
var results = new List<object>();
foreach (var scenario in scenarios.RootElement.EnumerateArray())
{
    var directory = Path.Combine(root, "pisharp-" + results.Count);
    Directory.CreateDirectory(directory);
    var log = Path.Combine(directory, "requests.log");
    var environment = new Dictionary<string, string>
    {
        ["MCP_FIXTURE_REQUEST_LOG"] = log,
        ["MCP_FIXTURE_STRUCTURED_JSON"] = scenario.GetProperty("json").GetString()!,
        ["MCP_FIXTURE_OMIT_CONTENT"] = scenario.GetProperty("omitContent").GetBoolean() ? "1" : "0"
    };
    await File.WriteAllTextAsync(Path.Combine(directory, "mcp.json"), JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            ["fixture"] = new { command = "python3", args = new[] { args[2] }, exposure = "direct", env = environment }
        }
    }));
    var configuration = await McpConfiguration.LoadAsync(directory, directory, false);
    using var catalog = ExtensionCatalog.Load(directory, directory, false, discover: false);
    var errors = await McpRuntime.RegisterAsync(configuration, catalog, directory);
    if (errors.Count != 0) throw new InvalidOperationException(string.Join('\n', errors));
    var atConnection = Counts(log);
    var tool = catalog.Registration.ToolDefinitions.Single(item => item.Function.Name == "mcp__fixture__echo");
    var result = (PiSharpToolResult)(await tool.Function.InvokeAsync(
        new AIFunctionArguments(new Dictionary<string, object?> { ["value"] = "unused" })))!;
    new ToolResultSchemaValidator(tool.OutputSchema).Validate(result);
    foreach (var name in new[] { "list_mcp_resources", "list_mcp_resource_templates" })
    {
        var list = catalog.Registration.ToolDefinitions.Single(item => item.Function.Name == name);
        if (!ToolResultOutput.TryReadContract(await list.Function.InvokeAsync(new AIFunctionArguments()), out var listed) || listed.IsError)
            throw new InvalidOperationException("On-demand resource listing failed.");
    }
    results.Add(new { text = result.Text, structuredContent = result.StructuredContent,
        isError = result.IsError, atConnection, afterDemand = Counts(log) });
}
Console.WriteLine(JsonSerializer.Serialize(results));

static object Counts(string log)
{
    var methods = File.ReadAllLines(log);
    return new
    {
        tools = methods.Count(method => method == "tools/list"),
        resources = methods.Count(method => method == "resources/list"),
        templates = methods.Count(method => method == "resources/templates/list"),
        calls = methods.Count(method => method == "tools/call")
    };
}
