using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeFixture;

public sealed class ExampleExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddCommand("native-fixture", (arguments, _) =>
            Task.FromResult("fixture command: " + arguments), "Run the native extension fixture command.");
        builder.AddTool(AIFunctionFactory.Create((string value) => "fixture tool: " + value,
            name: "native_fixture_tool", description: "Returns a fixture response for the supplied value."));
        builder.AddBeforeAgentStartHandler(static (eventData, context, _) =>
        {
            var systemPrompt = eventData.SystemPrompt + "\nFixture working directory: " + context.WorkingDirectory;
            return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(new(systemPrompt));
        });
    }
}
