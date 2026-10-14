namespace PiSharp.Runtime.Resources;

public sealed record ResourcePackageSource(string Source,
    IReadOnlyList<string>? Extensions = null, IReadOnlyList<string>? Skills = null,
    IReadOnlyList<string>? Prompts = null, IReadOnlyList<string>? Themes = null,
    bool Autoload = true, bool HasFilter = false);
