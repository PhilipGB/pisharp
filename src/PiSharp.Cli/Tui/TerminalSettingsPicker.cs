using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

/// <summary>Edits supported settings through the reusable searchable terminal selection overlay.</summary>
internal sealed class TerminalSettingsPicker(TerminalEditor editor, Func<IReadOnlyList<string>>? themeNames = null)
{
    private readonly Func<IReadOnlyList<string>> _themeNames = themeNames ?? (() => ["dark", "light"]);

    private sealed record Setting(string Id, string Label, string Description, bool UserOnly = false);

    private static readonly Setting[] s_settings =
    [
        new("defaultProjectTrust", "Default project trust", "Fallback decision for protected project resources.", UserOnly: true),
        new("httpProxy", "HTTP proxy", "User-wide HTTP proxy for PiSharp-managed HTTP clients; takes effect on the next launch.", UserOnly: true),
        new("defaultThinkingLevel", "Default thinking level", "Initial thinking level unless overridden by --thinking."),
        new("theme", "Theme", "Choose a terminal theme or follow the terminal appearance."),
        new("terminal.trueColor", "Terminal true color", "Choose 24-bit colors, 256-color output, or detect terminal support."),
        new("enableSkillCommands", "Skill commands", "Show /skill:name entries in slash command completion."),
        new("externalEditor", "External editor", "Command that edits the prompt file; blank uses VISUAL or EDITOR."),
        new("hideThinkingBlock", "Hide thinking", "Hide reasoning blocks in the interactive transcript."),
        new("images.blockImages", "Block images", "Replace provider-bound images with text while preserving saved history."),
        new("compaction.enabled", "Automatic compaction", "Summarize older whole turns before a prompt when the context budget is known."),
        new("quietStartup", "Quiet startup", "Hide the startup banner on the next launch."),
        new("steeringMode", "Steering mode", "How queued steering messages are delivered during an agent turn."),
        new("followUpMode", "Follow-up mode", "How queued follow-up messages are delivered after an agent turn."),
        new("httpIdleTimeoutMs", "HTTP idle timeout", "Maximum wait for provider response headers or body data; 0 disables this idle limit."),
        new("retry.provider.timeoutMs", "Provider request timeout", "Maximum duration for one provider request, in milliseconds."),
        new("retry.provider.maxRetries", "Provider request retries", "Retry transient requests in the provider SDK before PiSharp handles the failure."),
        new("markdown.codeBlockIndent", "Code block indent", "Literal prefix before each rendered code line.")
    ];

    public async Task ShowAsync(UserSettings userSettings, UserSettings? projectSettings,
        Func<bool, string, string?, Task<(UserSettings User, UserSettings? Project)>> save,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userSettings);
        ArgumentNullException.ThrowIfNull(save);
        var projectOptions = projectSettings is null ? null : new[]
        {
            new TerminalSelectionOption<bool>("user", false, "User settings", "Edit settings for this agent installation.", "user global"),
            new TerminalSelectionOption<bool>("project", true, "Current project settings", "Edit .pi/settings.json; project resources must be trusted.", "project workspace")
        };
        var scope = projectOptions is null
            ? false
            : editor.ShowSelectionList("Settings scope", projectOptions, selectedKey: "user")?.Option.Value;
        if (scope is null) return;

        string? selectedKey = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentSettings = scope.Value ? projectSettings! : userSettings;
            var options = s_settings.Where(setting => !setting.UserOnly || !scope.Value)
                .Select(setting => ToOption(setting, currentSettings)).ToList();
            if (projectOptions is not null)
                options.Insert(0, new TerminalSelectionOption<Setting?>("__scope", null,
                    "Change settings scope", "Choose user or current project settings.", "scope user project"));

            var selected = editor.ShowSelectionList(scope.Value ? "Project settings" : "User settings", options,
                selectedKey, emptyMessage: "No settings available");
            if (selected is null) return;
            if (selected.Option.Key == "__scope")
            {
                var nextScope = editor.ShowSelectionList("Settings scope", projectOptions!, scope.Value ? "project" : "user");
                if (nextScope is not null) scope = nextScope.Option.Value;
                selectedKey = null;
                continue;
            }

            if (selected.Option.Value is not { } setting) return;
            var currentValue = GetValue(currentSettings, setting.Id);
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
                    var savedSettings = await save(scope.Value, setting.Id, updatedValue);
                    userSettings = savedSettings.User;
                    projectSettings = savedSettings.Project;
                }
                selectedKey = setting.Id;
                continue;
            }
            var valueOptions = Values(setting, currentValue).Select(value => new TerminalSelectionOption<string?>(
                value.Key, value.Value, value.Label, value.Description)).ToArray();
            var choice = editor.ShowSelectionList($"{setting.Label} · {DisplayValue(currentValue)}", valueOptions,
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

            var updated = await save(scope.Value, setting.Id, choice.Option.Value);
            userSettings = updated.User;
            projectSettings = updated.Project;
            selectedKey = setting.Id;
        }
    }

    private static TerminalSelectionOption<Setting?> ToOption(Setting setting, UserSettings settings)
    {
        var current = GetValue(settings, setting.Id);
        return new(setting.Id, setting, $"{setting.Label} · {DisplayValue(current, setting.Id)}",
            setting.Description, $"{setting.Label} {setting.Id} {current}");
    }

    private static string? GetValue(UserSettings settings, string id) => id switch
    {
        "defaultProjectTrust" => settings.DefaultProjectTrust,
        "httpProxy" => settings.HttpProxy is null ? null : "configured",
        "defaultThinkingLevel" => settings.DefaultThinkingLevel,
        "theme" => settings.Theme,
        "terminal.trueColor" => settings.TerminalTrueColor,
        "enableSkillCommands" => settings.EnableSkillCommands?.ToString().ToLowerInvariant(),
        "externalEditor" => settings.ExternalEditor,
        "hideThinkingBlock" => settings.HideThinkingBlock?.ToString().ToLowerInvariant(),
        "images.blockImages" => settings.BlockImages?.ToString().ToLowerInvariant(),
        "compaction.enabled" => settings.Compaction?.Enabled?.ToString().ToLowerInvariant(),
        "quietStartup" => settings.QuietStartup?.ToString().ToLowerInvariant(),
        "steeringMode" => settings.SteeringMode?.ToSettingValue(),
        "followUpMode" => settings.FollowUpMode?.ToSettingValue(),
        "retry.provider.maxRetries" => settings.Retry?.Provider?.MaxRetries?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "retry.provider.timeoutMs" => settings.Retry?.Provider?.TimeoutMs?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "httpIdleTimeoutMs" => settings.HttpIdleTimeoutMs?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "markdown.codeBlockIndent" => settings.MarkdownCodeBlockIndent,
        _ => null
    };

    private static string DisplayValue(string? value, string? settingId = null)
    {
        if (value is null) return "inherit/default";
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
        if (setting.Id == "defaultProjectTrust")
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
