namespace PiSharp.Cli;

internal sealed record DefaultToolReloadPlan(CliArguments Arguments, IReadOnlyList<string>? ActiveToolNames);

internal static class DefaultToolReloadPolicy
{
    private static readonly string[] DefaultBuiltinTools = ["read", "bash", "edit", "write"];
    private static readonly HashSet<string> BuiltinTools = new(
        ["read", "bash", "edit", "write", "grep", "find", "ls"], StringComparer.Ordinal);

    public static bool UsesSettingsDefaults(CliArguments arguments) =>
        arguments.Tools is null && !arguments.NoTools;

    public static CliArguments ApplyStartupDefaults(CliArguments arguments, UserSettings settings,
        Func<string, string?> environment, bool preserveSessionModel)
    {
        var applied = settings.ApplyDefaults(arguments, environment, preserveSessionModel);
        return arguments.NoBuiltinTools && UsesSettingsDefaults(arguments) && applied.Tools is not null
            ? applied with { Tools = WithoutBuiltins(applied.Tools) }
            : applied;
    }

    public static DefaultToolReloadPlan Resolve(CliArguments arguments, bool usesSettingsDefaults,
        UserSettings previousSettings, UserSettings nextSettings, IEnumerable<string> activeToolNames,
        Func<string, string?> environment)
    {
        if (!usesSettingsDefaults) return new(arguments, null);

        var nextArguments = nextSettings.ApplyDefaults(arguments with { Tools = null }, environment,
            preserveSessionModel: true);
        if (arguments.NoBuiltinTools && nextArguments.Tools is not null)
            nextArguments = nextArguments with { Tools = WithoutBuiltins(nextArguments.Tools) };

        var previousDefaults = ResolveConfiguredDefaults(previousSettings).ToHashSet(StringComparer.Ordinal);
        var excludedTools = new HashSet<string>(arguments.ExcludeTools ?? [], StringComparer.Ordinal);
        bool CanActivate(string name) =>
            !excludedTools.Contains(name) && (!arguments.NoBuiltinTools || !BuiltinTools.Contains(name));
        var additions = ResolveConfiguredDefaults(nextSettings)
            .Where(name => !previousDefaults.Contains(name) && CanActivate(name));
        var active = activeToolNames.Where(CanActivate).Concat(additions)
            .Distinct(StringComparer.Ordinal).ToArray();
        return new(nextArguments, active);
    }

    private static IReadOnlyList<string> ResolveConfiguredDefaults(UserSettings settings) =>
        settings.ApplyDefaults(CliArguments.Parse([]), _ => null).Tools ?? DefaultBuiltinTools;

    private static IReadOnlyList<string> WithoutBuiltins(IReadOnlyList<string> tools) =>
        tools.Where(name => !BuiltinTools.Contains(name)).ToArray();
}
