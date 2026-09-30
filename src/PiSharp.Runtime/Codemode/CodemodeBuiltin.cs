using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Codemode;

/// <summary>Built-in programmatic orchestration over the session's callable tools.</summary>
public static class CodemodeBuiltin
{
    private const int MaximumInlineDescriptionCharacters = 12 * 1024;
    private const int MaximumNamespaceDescriptionCharacters = 1024;
    private const int MaximumMcpNamespacesInDescription = 128;

    public static BuiltinExtensionDefinition Definition { get; } = new("codemode", Configure);

    public static void Configure(ExtensionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var function = AIFunctionFactory.Create(ExecuteAsync, name: "codemode",
            description: "Run JavaScript in an isolated QuickJS/WASM sandbox to orchestrate callable tools.");
        registration.AddTool(new PiSharpToolRegistration(function, ToolExposure.ModelOnly, DefaultActive: false,
            PrepareLoadout: PrepareLoadout, AllowNestedInvocation: false));
    }

    private static ToolLoadoutChanges PrepareLoadout(ToolLoadoutSnapshot snapshot)
    {
        var description = new StringBuilder("Run an async JavaScript body inside QuickJS/WASM. " +
            "Use tools.<name>(args) for callable tools; text(value), image(dataUrl) and console.log add output; " +
            "exit() ends early. searchTools(query, options), describeTool(name) and describeNamespace(name) " +
            "discover callable tools and MCP namespace metadata; " +
            "models.getModelsOfType(type, provider), models.getAvailableOfType(type, provider), " +
            "models.getModelOfType(type, provider, id) inspect catalogs; models.classify(model, context) invokes classifiers. " +
            "Only provider/id select authority. Check stopReason/errorMessage for provider failures. " +
            "store(key, value) and load(key) keep branch-local JSON state. The script can return a value. " +
            "ALL_TOOLS lists every callable tool, including those omitted below. MCP namespaces are listed " +
            "without their tool schemas; use searchTools with a namespace or describeNamespace for details. " +
            "There are no host globals, " +
            "timers, imports, process or fetch. Execution is limited to 30 seconds, 32 MiB of guest heap " +
            "and 64 KiB of captured text output. A first line // @options: {\"max_output_tokens\":1000,\"timeout_ms\":1000} " +
            "sets the output budget or shortens the deadline; deadlines above 30 seconds use the safety ceiling.\n");
        var mcpNamespaces = snapshot.Callable
            .Where(tool => tool.Exposure != ToolExposure.Deferred && IsMcpNamespace(tool.Namespace))
            .Select(tool => tool.Namespace!)
            .GroupBy(toolNamespace => toolNamespace.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(toolNamespace => toolNamespace.Name, StringComparer.Ordinal)
            .ToArray();
        if (mcpNamespaces.Length > 0)
        {
            var namespaceListing = new StringBuilder("\nMCP server namespaces:");
            var listed = 0;
            foreach (var toolNamespace in mcpNamespaces.Take(MaximumMcpNamespacesInDescription))
            {
                var entry = new StringBuilder("\n### ").Append(toolNamespace.Name);
                if (!string.IsNullOrWhiteSpace(toolNamespace.Description))
                {
                    var summary = toolNamespace.Description.Trim();
                    if (summary.Length > MaximumNamespaceDescriptionCharacters)
                        summary = summary[..MaximumNamespaceDescriptionCharacters] + "…";
                    entry.Append('\n').Append(summary);
                }
                if (description.Length + namespaceListing.Length + entry.Length + 1 >
                    MaximumInlineDescriptionCharacters) break;
                namespaceListing.Append(entry);
                listed++;
            }
            if (listed < mcpNamespaces.Length)
                namespaceListing.Append("\nAdditional MCP namespaces are omitted here; use searchTools or describeNamespace.");
            if (description.Length + namespaceListing.Length + 1 <= MaximumInlineDescriptionCharacters)
                description.Append(namespaceListing).Append('\n');
        }
        foreach (var tool in snapshot.Callable.Where(tool => tool.Exposure != ToolExposure.Deferred &&
                     !IsMcpNamespace(tool.Namespace)).Take(128))
        {
            var sample = CodemodeToolCatalog.RenderToolSample(tool);
            if (description.Length + sample.Length > MaximumInlineDescriptionCharacters) break;
            description.Append('\n').Append("### ").Append(tool.Function.Name).Append('\n').Append(sample).Append('\n');
        }
        return new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["codemode"] = description.ToString()
        });
    }

    private static async Task<PiSharpToolResult> ExecuteAsync(
        [Description("JavaScript async function body. Top-level await and return are supported.")] string code,
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var context = PiSharpToolExecutionContext.Get(arguments) ??
            throw new InvalidOperationException("Codemode requires an active tool session.");
        var result = await CodemodeSandbox.ExecuteAsync(code, context, context.CodemodeStore, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Ok && result.Store is not null) context.SetCodemodeStore(result.Store);
        var usage = CodemodeUsageAccounting.Aggregate(context.NestedUsage);
        var text = result.Ok ? result.Text : result.Text.Length > 0 ? result.Text + "\n" + result.Error : result.Error ?? "Codemode failed.";
        long budget = 10000;
        try { budget = CodemodeSource.Parse(code).MaxOutputTokens; }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException) { }
        var rendered = await CodemodeOutputBudget.ApplyAsync(text, budget, cancellationToken);
        return new PiSharpToolResult(rendered.Text, new { sandbox = "quickjs-wasm", fullOutputPath = rendered.FullOutputPath },
            IsError: !result.Ok, Error: result.Error, Images: result.Images, Usage: usage.Usage, Cost: usage.Cost);
    }

    private static bool IsMcpNamespace(PiSharpToolNamespace? toolNamespace) =>
        toolNamespace?.Name.StartsWith("mcp__", StringComparison.Ordinal) == true;
}
