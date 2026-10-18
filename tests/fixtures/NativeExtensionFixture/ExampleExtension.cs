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
        builder.AddAgentStartHandler((_, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordLifecycleMarker($"native-fixture-agent-start-{FixtureRevision}");
            return ValueTask.CompletedTask;
        });
        NativeExtensionControlCommands.Register(builder, FixtureRevision);
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
        builder.AddCommand("native-fixture-state-set", async (value, context, cancellationToken) =>
        {
            var session = context.Session ?? throw new InvalidOperationException("The host did not provide session state.");
            await session.AppendEntryAsync("native-fixture-state",
                System.Text.Json.JsonSerializer.SerializeToElement(new { revision = FixtureRevision, value }),
                cancellationToken);
            return $"{FixtureRevision} state saved";
        }, "Append a value to the active session's durable extension state.");
        builder.AddCommand("native-fixture-state-show", (_, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = context.Session?.Entries
                .Where(entry => entry.CustomType == "native-fixture-state")
                .Select(entry => entry.Data.GetProperty("value").GetString() ?? "")
                .ToArray() ?? [];
            return Task.FromResult(entries.Length == 0 ? "no native fixture state" : string.Join("|", entries));
        }, "Show values reconstructed from the active session branch.");
        builder.AddCommand("native-fixture-mode", (_, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult($"{context.Mode}:{context.HasUserInterface}");
        }, "Report the current host mode and UI availability.");
        builder.AddCommand("native-fixture-ui", async (_, context, cancellationToken) =>
        {
            var environment = await context.Ui.SelectAsync("Fixture environment", ["Development", "Production"],
                cancellationToken);
            if (environment is null) return "native fixture UI selection cancelled";
            if (environment == "Production" && !await context.Ui.ConfirmAsync("Confirm", "Use production?",
                    cancellationToken))
                return "native fixture UI confirmation declined";

            var label = await context.Ui.InputAsync("Environment label", "fixture label", cancellationToken);
            if (label is null) return "native fixture UI input cancelled";
            context.Ui.Notify($"native-fixture-ui-{FixtureRevision}:{environment}:{label}");
            return $"{FixtureRevision} UI completed";
        }, "Exercise select, confirm, input and notification through the host UI.");
        builder.AddShortcut(PiSharpShortcutKey.Parse("ctrl+shift+g"), (context, _) =>
        {
            context.Ui.Notify($"native-fixture-shortcut-{FixtureRevision}");
            return Task.CompletedTask;
        }, description: "Show the native fixture shortcut marker.");
        builder.AddSessionStartHandler((eventData, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = context.Session?.Entries
                .Where(entry => entry.CustomType == "native-fixture-state")
                .Select(entry => entry.Data.GetProperty("value").GetString() ?? "") ?? [];
            var marker = $"native-fixture-session-start-{FixtureRevision}:{eventData.Reason}:{string.Join("|", values)}";
            RecordLifecycleMarker(marker);
            context.Ui.Notify(marker);
            return ValueTask.CompletedTask;
        });
        builder.AddSessionShutdownHandler((eventData, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var marker = $"native-fixture-session-shutdown-{FixtureRevision}:{eventData.Reason}";
            RecordLifecycleMarker(marker);
            context.Ui.Notify(marker);
            return ValueTask.CompletedTask;
        });
        builder.AddTool(AIFunctionFactory.Create((string value) =>
                $"{FixtureRevision} tool / private dependency {NativeDependencyInfo.Version}: {value}",
            name: "native_fixture_tool", description: "Returns a fixture response for the supplied value."));
        builder.AddBeforeAgentStartHandler(async (eventData, context, cancellationToken) =>
        {
            const string holdPrefix = "__native_fixture_hold_callback:";
            if (eventData.Prompt.StartsWith(holdPrefix, StringComparison.Ordinal))
            {
                var gatePath = eventData.Prompt[holdPrefix.Length..];
                await File.WriteAllTextAsync(gatePath + ".started", "started", cancellationToken);
                while (!File.Exists(gatePath + ".release"))
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }

            var structuredPrompt = eventData.Prompt.StartsWith("native-fixture-structured:", StringComparison.Ordinal);
            var systemPrompt = eventData.SystemPrompt + $"\n{FixtureRevision} extension / private dependency " +
                NativeDependencyInfo.Version + " / working directory: " + context.WorkingDirectory;
            var messageMarker = eventData.MessageType == PiSharpTurnMessageType.Custom
                ? $"native-fixture-{FixtureRevision}-before-custom:{eventData.StartingCustomMessage?.CustomType}"
                : $"native-fixture-{FixtureRevision}-before-user";
            systemPrompt += "\n" + messageMarker;
            return new PiSharpBeforeAgentStartResult(structuredPrompt ? null : systemPrompt)
            {
                InjectedMessage = eventData.MessageType == PiSharpTurnMessageType.User
                    ? new($"native-fixture-{FixtureRevision}-injected",
                        $"native-fixture-{FixtureRevision}-injected", Display: false)
                    : null
            };
        });
        builder.AddBeforeAgentStartHandler((eventData, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (eventData.Prompt.StartsWith("native-fixture-structured:", StringComparison.Ordinal))
            {
                eventData.SystemPromptOptions.Sections["addendum"] =
                    $"native-fixture-{FixtureRevision}-structured-prompt-options";
            }
            return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(null);
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

    private static void RecordLifecycleMarker(string marker)
    {
        var path = Environment.GetEnvironmentVariable("NATIVE_FIXTURE_LIFECYCLE_PATH");
        if (path is not null) File.AppendAllText(path, marker + Environment.NewLine);
    }
}
