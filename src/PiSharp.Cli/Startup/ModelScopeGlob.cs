using PiSharp.Runtime.Resources;

namespace PiSharp.Cli;

/// <summary>Bounded, case-insensitive minimatch-style matching for model scope patterns.</summary>
public static class ModelScopeGlob
{
    public static bool Matches(string pattern, string value) =>
        pattern.Length <= 512 && value.Length <= 2048 &&
        LocalResourcePathRules.GlobMatch(value, pattern, ignoreCase: true);
}
