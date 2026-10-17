using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.TestExtensions.NativePrivateDependency;

namespace PiSharp.TestExtensions.NativeFixture;

public sealed class ExampleExtension : IPiSharpExtension
{
#if NATIVE_FIXTURE_v2
    private const string FixtureRevision = "v2";
#else
    private const string FixtureRevision = "v1";
#endif

    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddCommand("native-fixture", (arguments, _) =>
            Task.FromResult($"{FixtureRevision} command / private dependency {NativeDependencyInfo.Version}: {arguments}"),
            "Run the native extension fixture command.");
        builder.AddTool(AIFunctionFactory.Create((string value) =>
                $"{FixtureRevision} tool / private dependency {NativeDependencyInfo.Version}: {value}",
            name: "native_fixture_tool", description: "Returns a fixture response for the supplied value."));
        builder.AddBeforeAgentStartHandler(static (eventData, context, _) =>
        {
            var systemPrompt = eventData.SystemPrompt + $"\n{FixtureRevision} extension / private dependency " +
                NativeDependencyInfo.Version + " / working directory: " + context.WorkingDirectory;
            return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(new(systemPrompt));
        });
    }
}
