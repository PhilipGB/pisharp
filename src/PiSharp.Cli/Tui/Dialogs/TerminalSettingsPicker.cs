using PiSharp.Cli;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

/// <summary>Edits supported settings through a compact, searchable in-flow settings list.</summary>
internal sealed class TerminalSettingsPicker(TerminalEditor editor, ProviderModelRuntime modelRuntime,
    Func<IReadOnlyList<string>>? themeNames = null)
{
    private readonly Func<IReadOnlyList<string>> _themeNames = themeNames ?? (() => ["system", "dark", "light"]);

    internal sealed record Setting(string Id, string Label, string Description, bool UserOnly = false);

    // Source receipt: Pi's settings-selector.ts at
    // https://github.com/earendil-works/pi/blob/main/packages/coding-agent/src/modes/interactive/components/settings-selector.ts
    // (main, inspected 2026-10-16; item array and insertion order).
    // The prompt's first-ten order is authoritative; later shared entries follow Pi's current
    // source order, including session sharing after follow-up mode (Pi's absent Transport item is omitted).
    // PiSharp-only options follow the shared settings.
    // Directly relevant Pi entries for Mermaid rendering, cache miss notices and changelog
    // collapsing are out of scope because PiSharp has no corresponding renderer, cache-cost
    // diagnostics or update-notification subsystem. Later supported entries retain source order.
    internal static readonly Setting[] Settings =
    [
        new("compaction.enabled", "Auto-compact", "Automatically compact context when it gets too large."),
        new("images.autoResize", "Auto-resize images", "Resize large images to 2000×2000 maximum before sending them to a model."),
        new("images.blockImages", "Block images", "Prevent images from being sent to models."),
        new("enableSkillCommands", "Skill commands", "Register skills as /skill:name commands."),
        new("showHardwareCursor", "Show hardware cursor", "Show the terminal cursor while positioning it for input methods."),
        new("editorPaddingX", "Editor padding", "Horizontal padding for the input editor, from 0 to 3 cells."),
        new("outputPad", "Output padding", "Horizontal padding for transcript content, from 0 to 1 cells."),
        new("autocompleteMaxVisible", "Autocomplete max items", "Maximum visible entries in the autocomplete list, from 3 to 20."),
        new("terminal.clearOnShrink", "Clear on shrink", "Clear empty terminal rows when rendered content shrinks."),
        new("terminal.showTerminalProgress", "Terminal progress", "Show OSC 9;4 progress indicators in the terminal tab bar."),
        new("steeringMode", "Steering mode", "How queued steering messages are delivered during an agent turn."),
        new("followUpMode", "Follow-up mode", "How queued follow-up messages are delivered after an agent turn."),
        new("sessionShareDestination", "Session share destination", "Where to upload sessions with /share (auto prefers Radius when authenticated, otherwise GitHub Gist)"),
        new("httpIdleTimeoutMs", "HTTP idle timeout", "Maximum wait for provider response headers or body data; 0 disables this idle limit."),
        new("hideThinkingBlock", "Hide thinking", "Hide reasoning blocks in the interactive transcript."),
        new("quietStartup", "Quiet startup", "Disable verbose printing at startup; header keeps only the startup header."),
        new("defaultProjectTrust", "Default project trust", "Fallback decision for protected project resources.", UserOnly: true),
        new("modelThinkingLevels", "Default thinking per model", "Override the default thinking level for a specific model.", UserOnly: true),
        new("theme", "Theme", "Choose the system theme, a named theme, or a light/dark pair."),
        new("fullscreenWheelScrollLines", "Fullscreen wheel scrolling", "Lines per mouse-wheel event in fullscreen mode; auto accelerates fast wheel spins where the terminal does not."),
        // PiSharp-only settings follow Pi's shared selector settings.
        new("defaultThinkingLevel", "Default thinking level", "Initial thinking level unless overridden by --thinking."),
        new("httpProxy", "HTTP proxy", "User-wide HTTP proxy for PiSharp-managed HTTP clients; takes effect on the next launch.", UserOnly: true),
        new("terminal.trueColor", "Terminal true color", "Choose 24-bit colors, 256-color output, or detect terminal support."),
        new("externalEditor", "External editor", "Command that edits the prompt file; blank uses VISUAL or EDITOR."),
        new("retry.provider.timeoutMs", "Provider request timeout", "Maximum duration for one provider request, in milliseconds."),
        new("retry.provider.maxRetries", "Provider request retries", "Retry transient provider requests before PiSharp handles the failure."),
        new("retry.provider.maxRetryDelayMs", "Provider retry delay limit", "Fail rather than wait when a provider requests a longer delay; 0 removes the limit."),
        new("markdown.codeBlockIndent", "Code block indent", "Literal prefix before each rendered code line.")
    ];

    public async Task ShowAsync(UserSettings userSettings, UserSettings? projectSettings,
        Func<bool, string, string?, Task<(UserSettings User, UserSettings? Project)>> save,
        CancellationToken cancellationToken = default, Func<string?>? activeThemeSetting = null,
        Func<ModelDescriptor, string?, Task<(UserSettings User, UserSettings? Project)>>? saveModelThinkingLevel = null,
        ModelDescriptor? currentModel = null)
    {
        ArgumentNullException.ThrowIfNull(userSettings);
        ArgumentNullException.ThrowIfNull(save);
        var scope = false;
        string? selectedKey = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSettings = scope ? projectSettings! : userSettings;
            var effectiveSettings = projectSettings is null ? userSettings : userSettings.Overlay(projectSettings);
            var options = Settings.Where(setting => !setting.UserOnly || !scope)
                .Select(setting => ToListItem(setting, currentSettings, effectiveSettings, scope,
                    setting.Id == "theme" && scope ? activeThemeSetting?.Invoke() : null)).ToArray();
            var selected = editor.ShowSettingsList("Settings", options, selectedKey,
                scope ? "Project settings" : "User settings", projectSettings is not null, cancellationToken);
            if (selected is null) return;
            if (selected.ChangeScope)
            {
                scope = !scope;
                selectedKey = null;
                continue;
            }

            if (selected.Item is not { } selectedItem) return;
            var setting = Settings.Single(item => item.Id == selectedItem.Key);
            if (setting.Id == "modelThinkingLevels")
            {
                if (saveModelThinkingLevel is not null)
                {
                    var savedModelSettings = await ShowModelThinkingDefaultsAsync(effectiveSettings, saveModelThinkingLevel,
                        currentModel, cancellationToken).ConfigureAwait(false);
                    if (savedModelSettings is { } saved)
                    {
                        userSettings = saved.User;
                        projectSettings = saved.Project;
                    }
                }
                selectedKey = setting.Id;
                continue;
            }
            if (selected.ResetValue)
            {
                var reset = await save(scope, setting.Id, null);
                userSettings = reset.User;
                projectSettings = reset.Project;
                selectedKey = setting.Id;
                continue;
            }

            var currentValue = setting.Id == "theme" && scope
                ? activeThemeSetting?.Invoke() ?? GetValue(currentSettings, setting.Id)
                : GetValue(currentSettings, setting.Id);
            if (IsBoolean(setting.Id))
            {
                var currentBoolean = GetBooleanValue(scope ? effectiveSettings : currentSettings, setting.Id);
                var updatedBoolean = await save(scope, setting.Id, (!currentBoolean).ToString().ToLowerInvariant());
                userSettings = updatedBoolean.User;
                projectSettings = updatedBoolean.Project;
                selectedKey = setting.Id;
                continue;
            }

            if (setting.Id is "externalEditor" or "httpProxy" or "retry.provider.timeoutMs" or "markdown.codeBlockIndent")
            {
                Console.WriteLine(setting.Id switch
                {
                    "httpProxy" => $"HTTP proxy [{DisplayValue(currentValue)}]; enter an HTTP(S) URL or leave blank to clear:",
                    "retry.provider.timeoutMs" => $"Provider request timeout in milliseconds [{DisplayValue(currentValue)}]; enter a nonnegative integer or leave blank for the stream-idle default:",
                    "markdown.codeBlockIndent" => $"Literal code line prefix [{DisplayValue(currentValue, setting.Id)}]; type spaces exactly as they should appear or leave blank to inherit:",
                    _ => $"External editor command [{DisplayValue(currentValue)}]; leave blank to use VISUAL/EDITOR or the default:"
                });
                var entered = await editor.ReadLineAsync(_ => Task.CompletedTask, enableApplicationActions: false);
                if (entered is null) return;
                var updatedValue = setting.Id == "markdown.codeBlockIndent"
                    ? entered.Length == 0 ? null : entered
                    : string.IsNullOrWhiteSpace(entered) ? null : entered.Trim();
                if (!string.Equals(updatedValue, currentValue, StringComparison.Ordinal))
                {
                    var savedSettings = await save(scope, setting.Id, updatedValue);
                    userSettings = savedSettings.User;
                    projectSettings = savedSettings.Project;
                }
                selectedKey = setting.Id;
                continue;
            }
            var valueOptions = Values(setting, currentValue).Select(value => new TerminalSelectionOption<string?>(
                value.Key, value.Value, value.Label, value.Description)).ToArray();
            var choice = editor.ShowSelectionList($"{setting.Label} · {DisplayValue(currentValue, setting.Id)}", valueOptions,
                KeyForValue(currentValue), emptyMessage: "No values available");
            if (choice is null)
            {
                selectedKey = setting.Id;
                continue;
            }

            if (string.Equals(choice.Option.Value, currentValue, StringComparison.Ordinal))
            {
                selectedKey = setting.Id;
                continue;
            }

            var updated = await save(scope, setting.Id, choice.Option.Value);
            userSettings = updated.User;
            projectSettings = updated.Project;
            selectedKey = setting.Id;
        }
    }

    private async Task<(UserSettings User, UserSettings? Project)?> ShowModelThinkingDefaultsAsync(UserSettings settings,
        Func<ModelDescriptor, string?, Task<(UserSettings User, UserSettings? Project)>> save, ModelDescriptor? currentModel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var models = modelRuntime.Providers
            .SelectMany(provider => provider.Models.Select(model => model with { Provider = provider.Id }))
            .Concat(modelRuntime.GetAvailableModelSnapshot())
            .Concat(modelRuntime.VirtualModels?.Models.Select(item => item.Model) ?? [])
            .Where(model => !string.IsNullOrWhiteSpace(model.Provider))
            .GroupBy(model => $"{model.Provider}/{model.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(model => currentModel is not null && model.Provider!.Equals(currentModel.Provider,
                StringComparison.OrdinalIgnoreCase) && model.Id.Equals(currentModel.Id, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(model => model.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var modelOptions = models.Select(model =>
        {
            var key = $"{model.Provider}/{model.Id}";
            var currentLevel = settings.GetModelThinkingLevel(model.Provider!, model.Id);
            return new TerminalSelectionOption<ModelDescriptor>(key, model,
                $"{model.Id} [{model.Provider}]", model.Name ?? model.Id,
                $"{model.Provider} {model.Name} {currentLevel}", IsCurrent: currentLevel is not null);
        }).ToArray();
        var selectedModel = await editor.ShowSelectionListAsync("Default Thinking · Model", modelOptions,
            currentModel is null ? null : $"{currentModel.Provider}/{currentModel.Id}",
            emptyMessage: "No models available", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (selectedModel is null) return null;

        var model = selectedModel.Option.Value;
        var overrideLevel = settings.GetModelThinkingLevel(model.Provider!, model.Id);
        var levelOptions = ThinkingLevels.AvailableForModel(model.Reasoning, model.ThinkingLevelMap)
            .Select(level => new TerminalSelectionOption<string?>(level, level,
                $"{(level.Equals(overrideLevel, StringComparison.OrdinalIgnoreCase) ? "✓ " : "  ")}{level}",
                ThinkingLevels.Description(level))).ToList();
        if (overrideLevel is not null)
            levelOptions.Add(new TerminalSelectionOption<string?>("__clear", null, "Clear override",
                $"Revert to the global default ({settings.DefaultThinkingLevel ?? ThinkingLevels.Default})."));
        var selectedLevel = await editor.ShowSelectionListAsync($"Thinking Default · {model.Provider}/{model.Id}",
            levelOptions, overrideLevel, emptyMessage: "No thinking levels available",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (selectedLevel is null) return null;
        if (selectedLevel.Option.Value is null && overrideLevel is null) return null;
        if (selectedLevel.Option.Value is { } selected && selected.Equals(overrideLevel, StringComparison.OrdinalIgnoreCase))
            return null;
        return await save(model, selectedLevel.Option.Value).ConfigureAwait(false);
    }

    private static TerminalSettingsListItem ToListItem(Setting setting, UserSettings settings,
        UserSettings effectiveSettings, bool projectScope, string? activeThemeSetting)
    {
        var current = GetValue(settings, setting.Id);
        string display;
        if (IsBoolean(setting.Id))
        {
            var resolved = GetBooleanValue(projectScope ? effectiveSettings : settings, setting.Id);
            display = resolved ? "true" : "false";
            if (current is null)
                display += projectScope ? " (inherited)" : " (default)";
        }
        else if (setting.Id is "editorPaddingX" or "outputPad" or "autocompleteMaxVisible" or "fullscreenWheelScrollLines")
        {
            var effective = GetValue(effectiveSettings, setting.Id);
            var configured = current ?? (projectScope ? effective : null);
            var resolved = configured ?? (setting.Id switch
            {
                "editorPaddingX" => "0",
                "outputPad" => "1",
                "autocompleteMaxVisible" => "5",
                "fullscreenWheelScrollLines" => "auto",
                _ => null
            });
            display = DisplayValue(resolved, setting.Id);
            if (current is null)
                display += projectScope && effective is not null ? " (inherited)" : " (default)";
        }
        else
        {
            var effective = GetValue(effectiveSettings, setting.Id);
            var themeValue = setting.Id == "theme" && current is null && projectScope &&
                activeThemeSetting is not null ? activeThemeSetting : current ?? (projectScope ? effective : null);
            display = DisplayValue(themeValue, setting.Id);
            if (current is null && setting.Id == "theme" && !projectScope)
                display = "system (default)";
            else if (projectScope && current is null && effective is not null)
                display += " (inherited)";
            else if (!projectScope && current is null && setting.Id != "theme")
                display += " (default)";
        }

        var search = $"{setting.Label} {setting.Id} {display}";
        return new(setting.Id, setting.Label, display, setting.Description, search);
    }

    private static bool IsBoolean(string id) => id is "compaction.enabled" or "images.autoResize" or
        "images.blockImages" or "enableSkillCommands" or "showHardwareCursor" or "terminal.clearOnShrink" or
        "terminal.showTerminalProgress" or "hideThinkingBlock";

    private static bool GetBooleanValue(UserSettings settings, string id) => id switch
    {
        "compaction.enabled" => settings.Compaction?.Enabled ?? true,
        "images.autoResize" => settings.AutoResizeImages ?? true,
        "images.blockImages" => settings.BlockImages ?? false,
        "enableSkillCommands" => settings.EnableSkillCommands ?? true,
        "showHardwareCursor" => settings.ShowHardwareCursor ?? false,
        "terminal.clearOnShrink" => settings.ClearOnShrink ?? false,
        "terminal.showTerminalProgress" => settings.ShowTerminalProgress ?? false,
        "hideThinkingBlock" => settings.HideThinkingBlock ?? false,
        _ => false
    };

    private static string? GetValue(UserSettings settings, string id) => id switch
    {
        "defaultProjectTrust" => settings.DefaultProjectTrust,
        "httpProxy" => settings.HttpProxy is null ? null : "configured",
        "defaultThinkingLevel" => settings.DefaultThinkingLevel,
        "modelThinkingLevels" => settings.ModelThinkingLevels is { Count: > 0 } levels
            ? $"{levels.Count} model{(levels.Count == 1 ? "" : "s")}" : "none",
        "theme" => settings.Theme,
        "terminal.trueColor" => settings.TerminalTrueColor,
        "images.autoResize" => settings.AutoResizeImages?.ToString().ToLowerInvariant(),
        "showHardwareCursor" => settings.ShowHardwareCursor?.ToString().ToLowerInvariant(),
        "editorPaddingX" => settings.EditorPaddingX?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "outputPad" => settings.OutputPad?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "autocompleteMaxVisible" => settings.AutocompleteMaxVisible?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "terminal.clearOnShrink" => settings.ClearOnShrink?.ToString().ToLowerInvariant(),
        "terminal.showTerminalProgress" => settings.ShowTerminalProgress?.ToString().ToLowerInvariant(),
        "enableSkillCommands" => settings.EnableSkillCommands?.ToString().ToLowerInvariant(),
        "externalEditor" => settings.ExternalEditor,
        "hideThinkingBlock" => settings.HideThinkingBlock?.ToString().ToLowerInvariant(),
        "images.blockImages" => settings.BlockImages?.ToString().ToLowerInvariant(),
        "compaction.enabled" => settings.Compaction?.Enabled?.ToString().ToLowerInvariant(),
        "quietStartup" => settings.QuietStartup?.ToSettingValue(),
        "sessionShareDestination" => settings.ShareDestination,
        "steeringMode" => settings.SteeringMode?.ToSettingValue(),
        "followUpMode" => settings.FollowUpMode?.ToSettingValue(),
        "retry.provider.maxRetries" => settings.Retry?.Provider?.MaxRetries?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "retry.provider.maxRetryDelayMs" => settings.Retry?.Provider?.MaxRetryDelayMs?.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
        "retry.provider.timeoutMs" => settings.Retry?.Provider?.TimeoutMs?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "httpIdleTimeoutMs" => settings.HttpIdleTimeoutMs?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "markdown.codeBlockIndent" => settings.MarkdownCodeBlockIndent,
        "fullscreenWheelScrollLines" => settings.FullscreenWheelScrollLines?.ToSettingValue(),
        _ => null
    };

    private static string DisplayValue(string? value, string? settingId = null)
    {
        if (value is null) return "inherit/default";
        if (settingId == "quietStartup") return value;
        if (settingId == "markdown.codeBlockIndent")
            return value.Length == 0 ? "no prefix" : value.All(character => character == ' ')
                ? $"{value.Length} space{(value.Length == 1 ? "" : "s")}"
                : value == "\t" ? "tab" : $"{value.Length} character prefix";
        return value switch
        {
            "true" => "enabled",
            "false" => "disabled",
            _ => value
        };
    }

    private static string KeyForValue(string? value) => value ?? "__inherit";

    private IReadOnlyList<(string Key, string? Value, string Label, string Description)> Values(Setting setting, string? currentValue)
    {
        var values = new List<(string Key, string? Value, string Label, string Description)>
        {
            ("__inherit", null, "Inherit / default", "Remove this override in the selected scope.")
        };
        if (setting.Id == "quietStartup")
        {
            values.Add(("true", "true", "true", "Hide the startup header and details."));
            values.Add(("header", "header", "header", "Keep only the startup header."));
            values.Add(("false", "false", "false", "Show the startup header and details."));
        }
        else if (setting.Id == "sessionShareDestination")
        {
            values.Clear();
            values.Add(("auto", "auto", "auto", "Use Radius when authenticated, otherwise GitHub Gist."));
            values.Add(("gist", "gist", "gist", "Always create a private GitHub Gist."));
            values.Add(("radius", "radius", "radius", "Upload to Radius; sign in with /login radius first."));
        }
        else if (setting.Id == "defaultProjectTrust")
        {
            values.Add(("ask", "ask", "Ask", "Prompt when no explicit trust decision exists."));
            values.Add(("always", "always", "Always trust", "Trust protected resources by default."));
            values.Add(("never", "never", "Never trust", "Require an explicit one-run approval."));
        }
        else if (setting.Id == "defaultThinkingLevel")
        {
            values.AddRange(ThinkingLevels.All.Select(level => (level, (string?)level, level, $"Use {level} as the default thinking level.")));
        }
        else if (setting.Id is "steeringMode" or "followUpMode")
        {
            values.Add(("one-at-a-time", "one-at-a-time", "One at a time", "Deliver one queued message at each available boundary."));
            values.Add(("all", "all", "All together", "Deliver all queued messages together at each available boundary."));
        }
        else if (setting.Id == "retry.provider.maxRetries")
        {
            var counts = Enumerable.Range(0, 4).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
            if (currentValue is not null) counts.Add(currentValue);
            values.AddRange(counts.OrderBy(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .Select(value => (value, (string?)value, value, value == "0"
                    ? "Disable provider retries and let PiSharp handle the failure."
                    : $"Allow up to {value} provider-level retry attempts.")));
        }
        else if (setting.Id == "retry.provider.maxRetryDelayMs")
        {
            (double Milliseconds, string Label)[] choices =
            [
                (0, "No limit"), (1_000, "1 second"), (5_000, "5 seconds"),
                (30_000, "30 seconds"), (60_000, "60 seconds (default)")
            ];
            if (currentValue is not null && double.TryParse(currentValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var current) &&
                choices.All(choice => choice.Milliseconds != current))
                values.Add((currentValue, currentValue, $"{currentValue} milliseconds", "Keep the current custom retry-delay limit."));
            values.AddRange(choices.Select(choice =>
            {
                var value = choice.Milliseconds.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
                return (value, (string?)value, choice.Label, choice.Milliseconds == 0
                    ? "Accept any server-requested retry delay."
                    : $"Fail when a server requests more than {choice.Label} of waiting.");
            }));
        }
        else if (setting.Id == "httpIdleTimeoutMs")
        {
            (int Milliseconds, string Label)[] choices =
            [
                (30_000, "30 seconds"), (60_000, "1 minute"), (120_000, "2 minutes"),
                (300_000, "5 minutes"), (0, "Disabled")
            ];
            if (currentValue is not null && int.TryParse(currentValue, out var current) &&
                choices.All(choice => choice.Milliseconds != current))
                values.Add((currentValue, currentValue, $"{currentValue} milliseconds", "Keep the current custom timeout."));
            values.AddRange(choices.Select(choice =>
                (choice.Milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    (string?)choice.Milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    choice.Label, choice.Milliseconds == 0
                        ? "Disable the provider HTTP idle limit."
                        : $"Allow up to {choice.Label} of idle time while waiting for provider response data.")));
        }
        else if (setting.Id == "editorPaddingX")
        {
            var choices = Enumerable.Range(0, 4).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
            if (currentValue is not null) choices.Add(currentValue);
            values.AddRange(choices.OrderBy(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .Select(value => (value, (string?)value, value, $"Use {value} cells of horizontal editor padding.")));
        }
        else if (setting.Id == "outputPad")
        {
            foreach (var value in new[] { "0", "1" })
                values.Add((value, value, value, $"Use {value} cells of transcript padding."));
        }
        else if (setting.Id == "autocompleteMaxVisible")
        {
            var choices = new[] { "3", "5", "7", "10", "15", "20" }.ToHashSet(StringComparer.Ordinal);
            if (currentValue is not null) choices.Add(currentValue);
            values.AddRange(choices.OrderBy(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .Select(value => (value, (string?)value, value, $"Show up to {value} autocomplete entries.")));
        }
        else if (setting.Id == "fullscreenWheelScrollLines")
        {
            var choices = new[] { "1", "2", "3", "5", "10" }.ToHashSet(StringComparer.Ordinal);
            if (currentValue is not null && currentValue != "auto") choices.Add(currentValue);
            values.Add(("auto", "auto", "Auto", "Accelerate fast wheel spins where the terminal does not."));
            values.AddRange(choices.OrderBy(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .Select(value => (value, (string?)value, value, $"Move {value} lines per wheel event.")));
        }
        else if (setting.Id == "theme")
        {
            values.Add(("light/dark", "light/dark", "Follow terminal appearance", "Use the light theme on light terminals and the dark theme on dark terminals."));
            var available = _themeNames().ToHashSet(StringComparer.Ordinal);
            if (currentValue is not null && !currentValue.Contains('/')) available.Add(currentValue);
            values.AddRange(available.Order(StringComparer.Ordinal)
                .Select(name => (name, (string?)name, name, $"Use the {name} color palette.")));
        }
        else if (setting.Id == "terminal.trueColor")
        {
            values.Add(("auto", "auto", "Auto detect", "Use the terminal capability detected from the environment."));
            values.Add(("true", "true", "Enabled", "Use 24-bit true-color theme values."));
            values.Add(("false", "false", "Disabled", "Limit theme output to the 256-color palette."));
        }
        else
        {
            values.Add(("true", "true", "Enabled", "Enable this setting in the selected scope."));
            values.Add(("false", "false", "Disabled", "Disable this setting in the selected scope."));
        }
        return values;
    }
}
