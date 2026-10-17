using System.Text.Json;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeFixture;

internal static class NativeExtensionControlCommands
{
    public static void Register(IPiSharpExtensionBuilder builder, string revision)
    {
        var flagPrefix = revision == "v2" ? "native-fixture-v2-" : "native-fixture-";
        var enabledFlag = flagPrefix + "enabled";
        var labelFlag = flagPrefix + "label";
        builder.RegisterFlag(enabledFlag, PiSharpExtensionFlagType.Boolean,
            "Enable the native extension fixture control path.", PiSharpExtensionFlagValue.FromBoolean(false));
        builder.RegisterFlag(labelFlag, PiSharpExtensionFlagType.String,
            "Fixture label used to verify extension string flags.", PiSharpExtensionFlagValue.FromString("default-label"));

        builder.AddCommand("native-fixture-controls", (_, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settings = context.GetSettings();
            var enabled = context.GetFlag(enabledFlag);
            var label = context.GetFlag(labelFlag);
            var model = context.GetModel();
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                revision,
                shellCommandPrefix = settings.TryGetProperty("shellCommandPrefix", out var shellCommandPrefix)
                    ? shellCommandPrefix.GetString()
                    : null,
                enabled = enabled?.BooleanValue,
                label = label?.StringValue,
                sessionName = context.Session?.SessionName,
                commands = context.GetCommands().Select(command => command.Name).Order(StringComparer.Ordinal),
                activeTools = context.GetActiveTools(),
                configuredTools = context.GetConfiguredTools().Select(tool => new
                {
                    tool.Name,
                    tool.IsActive,
                    exposure = tool.Exposure.ToString(),
                    tool.Source
                }),
                model = model is null ? null : new { model.ProviderId, model.ModelId, model.DisplayName },
                thinking = context.GetThinkingLevel().ToString()
            }));
        }, "Read the fixture's settings, flags, command/tool catalogue, model and thinking level.");

        builder.AddCommand("native-fixture-set-active-tools", (arguments, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tools = arguments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            context.SetActiveTools(tools);
            return Task.FromResult(string.Join(',', context.GetActiveTools()));
        }, "Select the named tools for the next provider request.");

        builder.AddCommand("native-fixture-select-model", async (arguments, context, cancellationToken) =>
        {
            var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) throw new ArgumentException("Expected '<provider-id> <model-id>'.", nameof(arguments));
            var result = await context.SetModelAsync(parts[0], parts[1], cancellationToken);
            return result.Status + (result.Error is null ? "" : ": " + result.Error);
        }, "Select a session-local model by provider and model ID.");

        builder.AddCommand("native-fixture-set-thinking", async (arguments, context, cancellationToken) =>
        {
            if (!Enum.TryParse<PiSharpThinkingLevel>(arguments, ignoreCase: true, out var level))
                throw new ArgumentException("Expected a PiSharp thinking level.", nameof(arguments));
            var selected = await context.SetThinkingLevelAsync(level, cancellationToken);
            return selected.ToString();
        }, "Set the current session's thinking level.");

        builder.AddCommand("native-fixture-set-session-name", async (arguments, context, cancellationToken) =>
        {
            var session = context.Session ?? throw new InvalidOperationException("The host did not provide session metadata.");
            await session.SetSessionNameAsync(arguments, cancellationToken);
            return session.SessionName ?? "";
        }, "Set the current session's durable display name.");

        builder.AddCommand("native-fixture-label-entry", async (arguments, context, cancellationToken) =>
        {
            var session = context.Session ?? throw new InvalidOperationException("The host did not provide session metadata.");
            var separator = arguments.IndexOf(' ');
            var operation = separator < 0 ? arguments : arguments[..separator];
            var value = separator < 0 ? "" : arguments[(separator + 1)..];
            if (operation.Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                var entry = await session.AppendEntryAsync("native-fixture-label-target",
                    JsonSerializer.SerializeToElement(new { revision }), cancellationToken);
                await session.SetLabelAsync(entry.Id, value, cancellationToken);
                return entry.Id;
            }

            if (operation.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                await session.SetLabelAsync(value, null, cancellationToken);
                return "label cleared";
            }

            throw new ArgumentException("Expected 'set <label>' or 'clear <entry-id>'.", nameof(arguments));
        }, "Set or clear a label on a canonical session entry ID.");

        builder.AddCommand("native-fixture-abort", (_, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.AbortCurrentRun();
            return Task.FromResult("abort requested");
        }, "Abort the current provider run through the host cancellation owner.");

        builder.AddCommand("native-fixture-compact", async (_, context, cancellationToken) =>
        {
            var result = await context.CompactCurrentSessionAsync(cancellationToken);
            return result.Status + (result.Error is null ? "" : ": " + result.Error);
        }, "Compact the current session through the host's normal path.");

        builder.AddCommand("native-fixture-shutdown", (_, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.RequestShutdown();
            return Task.FromResult("shutdown requested");
        }, "Request graceful host shutdown.");
    }
}
