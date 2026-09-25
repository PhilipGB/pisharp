using PiSharp.Cli;

namespace PiSharp.Cli.Tui;

/// <summary>Edits supported settings through the reusable searchable terminal selection overlay.</summary>
internal sealed class TerminalSettingsPicker(TerminalEditor editor, Func<IReadOnlyList<string>>? themeNames = null)
{
    private readonly Func<IReadOnlyList<string>> _themeNames = themeNames ?? (() => ["dark", "light"]);

    private sealed record Setting(string Id, string Label, string Description, bool UserOnly = false);

    private static readonly Setting[] s_settings =
    [
        new("defaultProjectTrust", "Default project trust", "Fallback decision for protected project resources.", UserOnly: true),
        new("defaultThinkingLevel", "Default thinking level", "Initial thinking level unless overridden by --thinking."),
        new("theme", "Theme", "Choose a terminal theme or follow the terminal appearance."),
        new("externalEditor", "External editor", "Command that edits the prompt file; blank uses VISUAL or EDITOR."),
        new("hideThinkingBlock", "Hide thinking", "Hide reasoning blocks in the interactive transcript."),
        new("images.blockImages", "Block images", "Replace provider-bound images with text while preserving saved history."),
        new("compaction.enabled", "Automatic compaction", "Summarize older whole turns before a prompt when the context budget is known."),
        new("quietStartup", "Quiet startup", "Hide the startup banner on the next launch.")
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
            if (setting.Id == "externalEditor")
            {
                Console.WriteLine($"External editor command [{DisplayValue(currentValue)}]; leave blank to use VISUAL/EDITOR or the default:");
                var entered = await editor.ReadLineAsync(_ => Task.CompletedTask, enableApplicationActions: false);
                if (entered is null) return;
                var command = string.IsNullOrWhiteSpace(entered) ? null : entered.Trim();
                if (!string.Equals(command, currentValue, StringComparison.Ordinal))
                {
                    var savedSettings = await save(scope.Value, setting.Id, command);
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
        return new(setting.Id, setting, $"{setting.Label} · {DisplayValue(current)}",
            setting.Description, $"{setting.Label} {setting.Id} {current}");
    }

    private static string? GetValue(UserSettings settings, string id) => id switch
    {
        "defaultProjectTrust" => settings.DefaultProjectTrust,
        "defaultThinkingLevel" => settings.DefaultThinkingLevel,
        "theme" => settings.Theme,
        "externalEditor" => settings.ExternalEditor,
        "hideThinkingBlock" => settings.HideThinkingBlock?.ToString().ToLowerInvariant(),
        "images.blockImages" => settings.BlockImages?.ToString().ToLowerInvariant(),
        "compaction.enabled" => settings.Compaction?.Enabled?.ToString().ToLowerInvariant(),
        "quietStartup" => settings.QuietStartup?.ToString().ToLowerInvariant(),
        _ => null
    };

    private static string DisplayValue(string? value) => value switch
    {
        null => "inherit/default",
        "true" => "enabled",
        "false" => "disabled",
        _ => value
    };

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
        else if (setting.Id == "theme")
        {
            values.Add(("light/dark", "light/dark", "Follow terminal appearance", "Use the light theme on light terminals and the dark theme on dark terminals."));
            var available = _themeNames().ToHashSet(StringComparer.Ordinal);
            if (currentValue is not null && !currentValue.Contains('/')) available.Add(currentValue);
            values.AddRange(available.Order(StringComparer.Ordinal)
                .Select(name => (name, (string?)name, name, $"Use the {name} color palette.")));
        }
        else
        {
            values.Add(("true", "true", "Enabled", "Enable this setting in the selected scope."));
            values.Add(("false", "false", "Disabled", "Disable this setting in the selected scope."));
        }
        return values;
    }
}
