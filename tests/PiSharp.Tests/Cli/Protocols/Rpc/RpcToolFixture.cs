using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Tests;

public sealed class RpcToolFixture : IPiSharpExtension
{
    public void Configure(ExtensionRegistration registration)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PISHARP_RPC_TOOL_FIXTURE"), "true",
                StringComparison.OrdinalIgnoreCase))
            return;

        registration.AddTool(AIFunctionFactory.Create(async (string value, int delayMs, bool emitPartial,
            bool fail, AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        {
            if (emitPartial)
            {
                var context = PiSharpToolExecutionContext.Get(arguments) ??
                    throw new InvalidOperationException("PiSharp tool execution context was not attached.");
                context.ReportProgress("partial:" + value);
            }

            if (delayMs > 0) await Task.Delay(delayMs, cancellationToken);
            if (fail) return new PiSharpToolResult("fixture tool failed", Details: new Dictionary<string, object?>(),
                IsError: true, Error: "fixture tool failed");
            return new PiSharpToolResult("result:" + value);
        }, name: "rpc_parity_tool", description: "Return a deterministic result for RPC parity tests."));

        registration.AddTool(AIFunctionFactory.Create(async (AIFunctionArguments arguments,
            CancellationToken cancellationToken) =>
        {
            var context = PiSharpToolExecutionContext.Get(arguments) ??
                throw new InvalidOperationException("PiSharp tool execution context was not attached.");
            var result = await context.ExecuteToolAsync("rpc_parity_tool", new Dictionary<string, object?>
            {
                ["value"] = "nested-child-result",
                ["delayMs"] = 0,
                ["emitPartial"] = false,
                ["fail"] = false
            }, cancellationToken);
            return new PiSharpToolResult("nested child: " + result.Text);
        }, name: "rpc_nested_orchestrator", description: "Execute one nested RPC parity tool."));
    }
}
