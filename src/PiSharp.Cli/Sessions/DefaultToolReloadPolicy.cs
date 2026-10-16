namespace PiSharp.Cli;

internal sealed record DefaultToolReloadPlan(CliArguments Arguments, IReadOnlyList<string>? ActiveToolNames);

internal static class DefaultToolReloadPolicy
{
    private static readonly string[] DefaultBuiltinTools = ["read", "bash", "edit", "write"];
    private static readonly HashSet<string> BuiltinTools = new(
        ["read", "bash", "edit", "write", "grep", "find", "ls"], StringComparer.Ordinal);

    public static bool UsesSettingsDefaults(CliArguments arguments) =>
        arguments.UsesDefaultToolSelection ??
        ((arguments.Tools is null || arguments.Tools.Any(IsModifier)) && !arguments.NoTools);

    private static bool IsModifier(string name) => name.StartsWith('+') || name.StartsWith('-');

    internal static void ValidateToolSelection(IReadOnlyList<string> names)
    {
        var modifiers = names.Where(IsModifier).ToArray();
        if (modifiers.Length == 0) return;
        if (modifiers.Length != names.Count)
            throw new ArgumentException("Invalid tools option: tool names cannot be mixed with +name or -name entries");
        if (modifiers.FirstOrDefault(name => name.Contains('*')) is { } pattern)
            throw new ArgumentException($"Invalid tools option: +name and -name entries take exact tool names, not patterns: {pattern}");
    }

    internal static IReadOnlyList<string> ApplyModifiers(IEnumerable<string> names, IReadOnlyList<string>? modifiers)
    {
        var selected = names.ToList();
        foreach (var modifier in modifiers ?? [])
        {
            var name = modifier[1..];
            if (modifier[0] == '+' && name.Length > 0 && !selected.Contains(name)) selected.Add(name);
            else if (modifier[0] == '-') selected.Remove(name);
        }
        return selected;
    }

    internal static IReadOnlyList<string>? ResolveResumeTools(CliArguments arguments,
        IReadOnlyList<string>? recorded, IEnumerable<string> initialDeclarations)
    {
        if (!UsesSettingsDefaults(arguments) || arguments.NoBuiltinTools)
            return initialDeclarations.ToArray();
        return recorded is null ? null : ApplyModifiers(recorded, arguments.DefaultToolModifiers);
    }

    public static CliArguments ApplyStartupDefaults(CliArguments arguments, UserSettings settings,
        Func<string, string?> environment, bool preserveSessionModel)
    {
        var usesDefaults = UsesSettingsDefaults(arguments);
        var modifiers = arguments.DefaultToolModifiers ??
            (arguments.Tools?.Any(IsModifier) == true ? arguments.Tools : null);
        var applied = settings.ApplyDefaults(modifiers is null ? arguments : arguments with { Tools = null },
            environment, preserveSessionModel) with
        {
            UsesDefaultToolSelection = usesDefaults,
            DefaultToolModifiers = modifiers
        };
        if (modifiers is not null)
            applied = applied with { Tools = ApplyModifiers(applied.Tools ?? (arguments.NoTools ? [] : DefaultBuiltinTools), modifiers) };
        return arguments.NoBuiltinTools && UsesSettingsDefaults(arguments) && applied.Tools is not null
            ? applied with { Tools = WithoutBuiltins(applied.Tools) }
            : applied;
    }

    public static DefaultToolReloadPlan Resolve(CliArguments arguments, bool usesSettingsDefaults,
        UserSettings previousSettings, UserSettings nextSettings, IEnumerable<string> activeToolNames,
        Func<string, string?> environment)
    {
        if (!usesSettingsDefaults) return new(arguments, null);

        var nextArguments = ApplyStartupDefaults(arguments with { Tools = null }, nextSettings, environment,
            preserveSessionModel: true);
        if (arguments.NoBuiltinTools && nextArguments.Tools is not null)
            nextArguments = nextArguments with { Tools = WithoutBuiltins(nextArguments.Tools) };

        var previousDefaults = ApplyModifiers(ResolveConfiguredDefaults(previousSettings), arguments.DefaultToolModifiers)
            .ToHashSet(StringComparer.Ordinal);
        var excludedTools = new HashSet<string>(arguments.ExcludeTools ?? [], StringComparer.Ordinal);
        bool CanActivate(string name) =>
            !excludedTools.Contains(name) && (!arguments.NoBuiltinTools || !BuiltinTools.Contains(name));
        var additions = ApplyModifiers(ResolveConfiguredDefaults(nextSettings), arguments.DefaultToolModifiers)
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
