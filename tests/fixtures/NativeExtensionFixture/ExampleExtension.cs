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
        builder.AddCommand("native-fixture-send", async (arguments, context, cancellationToken) =>
        {
            var separator = arguments.IndexOf(' ');
            var operation = separator < 0 ? arguments : arguments[..separator];
            var content = separator < 0 ? "" : arguments[(separator + 1)..];
            if (operation.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                await context.SendUserMessageAsync(content, cancellationToken);
            }
            else if (operation.Equals("custom", StringComparison.OrdinalIgnoreCase))
            {
                await context.SendCustomMessageAsync(
                    new($"native-fixture-{FixtureRevision}-command", content),
                    triggerTurn: true, PiSharpMessageDeliveryMode.Steer, cancellationToken);
            }
            else
            {
                throw new ArgumentException("Expected 'user <text>' or 'custom <text>'.", nameof(arguments));
            }

            return $"{FixtureRevision} command sent {operation}";
        }, "Send user text or a custom message through the host conversation.");
        builder.AddTool(AIFunctionFactory.Create((string value) =>
                $"{FixtureRevision} tool / private dependency {NativeDependencyInfo.Version}: {value}",
            name: "native_fixture_tool", description: "Returns a fixture response for the supplied value."));
        builder.AddBeforeAgentStartHandler(static (eventData, context, _) =>
        {
            var systemPrompt = eventData.SystemPrompt + $"\n{FixtureRevision} extension / private dependency " +
                NativeDependencyInfo.Version + " / working directory: " + context.WorkingDirectory;
            var messageMarker = eventData.MessageType == PiSharpTurnMessageType.Custom
                ? $"native-fixture-{FixtureRevision}-before-custom:{eventData.StartingCustomMessage?.CustomType}"
                : $"native-fixture-{FixtureRevision}-before-user";
            systemPrompt += "\n" + messageMarker;
            return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(new(systemPrompt)
            {
                InjectedMessage = eventData.MessageType == PiSharpTurnMessageType.User
                    ? new($"native-fixture-{FixtureRevision}-injected",
                        $"native-fixture-{FixtureRevision}-injected", Display: false)
                    : null
            });
        });
        builder.AddContextTransform((messages, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transformed = messages.Select(message => message.Clone()).ToList();
            var lastUser = transformed.FindLastIndex(message => message.Role == ChatRole.User);
            if (lastUser >= 0)
                transformed[lastUser].Contents.Add(new TextContent($" [native-fixture-{FixtureRevision}-transform]"));
            return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(transformed);
        });
        builder.AddToolCallHook((context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (context.Arguments.TryGetValue("value", out var value))
                context.Arguments["value"] = $"native-fixture-{FixtureRevision}-{value}";
            return ValueTask.FromResult(PiSharpToolCallDecision.Allow);
        });
        builder.AddToolResultHook((context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Result = $"native-fixture-{FixtureRevision}-result:{context.Result}";
            return ValueTask.CompletedTask;
        });
        builder.AddResourceDiscoveryHandler((context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var phase = context.Reason == PiSharpResourceDiscoveryReason.Reload ? "reload" : "startup";
            var assemblyDirectory = Path.GetDirectoryName(typeof(ExampleExtension).Assembly.Location)!;
            var resources = Path.Combine(assemblyDirectory, "Resources", FixtureRevision);
            var resourcePhase = Path.Combine(resources, phase);
            return Task.FromResult<PiSharpResourceDiscoveryResult?>(new(
                SkillPaths: [Path.Combine(resourcePhase, "skills")],
                PromptPaths: [Path.Combine(resourcePhase, "prompts")],
                ThemePaths: [Path.Combine(resources, "themes")]));
        });
    }
}
