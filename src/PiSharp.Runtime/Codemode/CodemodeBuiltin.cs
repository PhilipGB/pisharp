using System.ComponentModel;
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
        registration.AddTool(new PiSharpToolRegistration(function, ToolExposure.ModelOnly,
            PrepareLoadout: PrepareLoadout, AllowNestedInvocation: false));
    }

    private static ToolLoadoutChanges PrepareLoadout(ToolLoadoutSnapshot snapshot)
    {
        var available = snapshot.Callable.Where(tool => tool.Function.Name != "codemode").Take(128)
            .Select(tool => $"- {tool.Function.Name}: {FirstLine(tool.Function.Description)}");
        return new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["codemode"] = "Run an async JavaScript body inside QuickJS/WASM. Use tools.<name>(args) for callable tools; " +
                "text(value), image(dataUrl) and console.log add output; exit() ends early. " +
                "searchTools(query) and describeTool(name) inspect callable tools; " +
                "store(key, value) and load(key) keep branch-local JSON state. " +
                "The script can return a value. There are no host globals, timers, imports, process or fetch. " +
                "Execution is limited to 30 seconds, 32 MiB of guest heap and 64 KiB of output.\n" +
                "Callable tools:\n" + string.Join('\n', available)
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
        return result.Ok
            ? new PiSharpToolResult(result.Text, new { sandbox = "quickjs-wasm" }, Images: result.Images)
            : new PiSharpToolResult(result.Text.Length > 0 ? result.Text + "\n" + result.Error : result.Error ?? "Codemode failed.",
                new { sandbox = "quickjs-wasm" }, IsError: true, Error: result.Error, Images: result.Images);
    }

    private static string FirstLine(string? description)
    {
        var line = (description ?? "").Split(['\r', '\n'], 2)[0].Trim();
        return line.Length > 120 ? line[..120] : line;
    }
}
