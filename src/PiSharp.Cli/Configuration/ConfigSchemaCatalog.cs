namespace PiSharp.Cli;

/// <summary>The pinned Pi configuration schemas shipped as files and embedded resources.</summary>
public static class ConfigSchemaCatalog
{
    private static readonly IReadOnlyList<string> s_names = Array.AsReadOnly(new[] { "models", "settings", "keybindings", "theme" });
    public static IReadOnlyList<string> Names => s_names;

    public static Stream Open(string name)
    {
        if (!s_names.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("Unknown configuration schema.", nameof(name));
        return typeof(ConfigSchemaCatalog).Assembly.GetManifestResourceStream($"PiSharp.Cli.Configuration.Schemas.{name}.schema.json")
            ?? throw new InvalidOperationException($"The {name} configuration schema resource is missing.");
    }
}
