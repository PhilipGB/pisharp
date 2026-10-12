using ModelContextProtocol.Client;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Mcp;

/// <summary>Publishes MCP tool names, exposure, namespaces, schemas and Pi-compatible renderers.</summary>
internal static class McpToolPublication
{
    public static void Register(McpServerConfiguration server, McpServerConnection connection,
        ExtensionRegistration registration, IList<McpClientTool> tools, string? instructions)
    {
        var owner = "mcp:" + server.Name;
        var ownedNames = registration.GetOwnedToolNames(owner).ToHashSet(StringComparer.Ordinal);
        var taken = registration.ToolDefinitions.Select(item => item.Function.Name)
            .Where(name => !ownedNames.Contains(name)).ToHashSet(StringComparer.Ordinal);
        var definitions = new List<PiSharpToolRegistration>();
        var renderers = new Dictionary<string, PiSharpToolRenderer>(StringComparer.Ordinal);
        var toolNames = McpToolIdentifiers.CreateNames(server.Name, tools.Select(tool => tool.Name).ToArray(), taken);
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            var name = toolNames[index];
            taken.Add(name);
            var exposure = server.ExposureFor(tool.Name);
            var function = new McpToolFunction(tool, connection, name, server.Timeout);
            definitions.Add(new PiSharpToolRegistration(function, MapExposure(exposure),
                DefaultActive: exposure == McpToolExposure.Direct,
                Namespace: new PiSharpToolNamespace(McpToolIdentifiers.Namespace(server.Name),
                    server.Description?.Trim(), instructions?.Trim(), [server.Name]),
                AllowNestedInvocation: exposure is McpToolExposure.Codemode or McpToolExposure.CodemodeDeferred,
                OutputSchema: McpCallToolResultSchema.Create(tool.ProtocolTool.OutputSchema)));
            var label = server.Name + "/" + tool.Name;
            renderers.Add(name, new PiSharpToolRenderer(
                renderCall: (arguments, context) =>
                    PiSharpToolCallDisplay.Format(label, arguments, context.IsExpanded),
                renderResult: RenderResult));
        }
        var source = new ResourceSourceInfo("builtin:mcp", "builtin", "builtin", "top-level", null);
        registration.ReplaceOwnedTools(owner, definitions, source, renderers);
    }

    public static ToolExposure MapExposure(McpToolExposure value) => value switch
    {
        McpToolExposure.Direct => ToolExposure.Direct,
        McpToolExposure.Deferred => ToolExposure.Deferred,
        McpToolExposure.Codemode or McpToolExposure.CodemodeDeferred => ToolExposure.CodeMode,
        _ => ToolExposure.Hidden
    };

    private static PiSharpToolRenderView RenderResult(PiSharpToolRenderResult result,
        PiSharpToolRenderContext context)
    {
        var output = (result.IsError ? result.Error ?? result.Text : result.Text)?.Trim();
        if (string.IsNullOrEmpty(output))
            return PiSharpToolRenderView.FromText("← (no output)", PiSharpToolTextStyle.Muted);
        var lines = output.Replace("\t", "    ", StringComparison.Ordinal).Split('\n');
        var shown = context.IsExpanded ? lines : lines.Take(5).ToArray();
        var spans = new List<PiSharpToolTextSpan>
        {
            new("← ", PiSharpToolTextStyle.Muted),
            new(string.Join('\n', shown), result.IsError ? PiSharpToolTextStyle.Error : PiSharpToolTextStyle.Output)
        };
        if (shown.Length < lines.Length)
            spans.Add(new($"\n... ({lines.Length - shown.Length} more lines; expand to view)",
                PiSharpToolTextStyle.Muted));
        return new(spans);
    }
}
