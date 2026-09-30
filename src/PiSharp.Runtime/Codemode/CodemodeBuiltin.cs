using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Codemode;

/// <summary>Built-in programmatic orchestration over the session's callable tools.</summary>
public static class CodemodeBuiltin
{
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
            "exit() ends early. searchTools(query, options) and describeTool(name) inspect callable tools; " +
            "models.getModelsOfType(type, provider), models.getAvailableOfType(type, provider), " +
            "models.getModelOfType(type, provider, id) inspect catalogs; models.classify(model, context) invokes classifiers. " +
            "Only provider/id select authority. Check stopReason/errorMessage for provider failures. " +
            "store(key, value) and load(key) keep branch-local JSON state. The script can return a value. " +
            "ALL_TOOLS lists every callable tool, including those omitted below. There are no host globals, " +
            "timers, imports, process or fetch. Execution is limited to 30 seconds, 32 MiB of guest heap " +
            "and 64 KiB of captured text output. A first line // @options: {\"max_output_tokens\":1000,\"timeout_ms\":1000} " +
            "sets the output budget or shortens the deadline; deadlines above 30 seconds use the safety ceiling.\n");
        foreach (var tool in snapshot.Callable.Where(tool => tool.Exposure != ToolExposure.Deferred).Take(128))
        {
            var sample = CodemodeToolCatalog.RenderToolSample(tool);
            if (description.Length + sample.Length > 12 * 1024) break;
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
}
