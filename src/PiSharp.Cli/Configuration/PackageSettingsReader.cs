using System.Text.Json;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli;

internal static class PackageSettingsReader
{
    internal static IReadOnlyList<ResourcePackageSource> Read(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("settings.json packages must be an array.");
        var packages = new List<ResourcePackageSource>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                packages.Add(new(entry.GetString()!));
                continue;
            }
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("source", out var source) ||
                source.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("settings.json packages entries require a string source.");
            packages.Add(new(source.GetString()!, Paths(entry, "extensions"), Paths(entry, "skills"),
                Paths(entry, "prompts"), Paths(entry, "themes"),
                !entry.TryGetProperty("autoload", out var autoload) || autoload.ValueKind != JsonValueKind.False,
                HasFilter: true));
        }
        return packages;
    }

    private static IReadOnlyList<string>? Paths(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new InvalidDataException($"settings.json package {name} must be a string array.");
        return value.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
}
