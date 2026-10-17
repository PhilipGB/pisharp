using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeApiV1;

public sealed class LegacyExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddCommand("api-v1-legacy", (arguments, _) =>
            Task.FromResult("api-v1 command: " + arguments));
        builder.AddTool(AIFunctionFactory.Create((string value) => "api-v1 tool: " + value,
            name: "api_v1_legacy_tool"));
        builder.AddBeforeAgentStartHandler((eventData, _, _) =>
            ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(
                new(eventData.SystemPrompt + "\napi-v1 before-agent-start: " + eventData.Prompt)));
    }
}
