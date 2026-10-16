using System.Text.Json;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli;

internal static class PackageSettingsReader
{
    internal static IReadOnlyList<string> ReadNpmCommand(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("settings.json npmCommand must be a string array.");
        var command = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } argument)
                throw new InvalidDataException("settings.json npmCommand entries must be strings.");
            command.Add(argument);
        }
        return command;
    }

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

    internal static IReadOnlyList<NativeExtensionPackageSource> ReadNativeExtensions(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 512)
            throw new InvalidDataException("settings.json nativeExtensions must be an array of at most 512 packages.");
        var packages = new List<NativeExtensionPackageSource>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("settings.json nativeExtensions entries must be objects.");
            var fields = new HashSet<string>(StringComparer.Ordinal);
            string? id = null, version = null, source = null;
            foreach (var property in entry.EnumerateObject())
            {
                if (!fields.Add(property.Name))
                    throw new InvalidDataException($"settings.json nativeExtensions contains duplicate property '{property.Name}'.");
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { } text)
                    throw new InvalidDataException($"settings.json nativeExtensions {property.Name} must be a string.");
                switch (property.Name)
                {
                    case "id": id = text; break;
                    case "version": version = text; break;
                    case "source": source = text; break;
                    default: throw new InvalidDataException($"settings.json nativeExtensions contains unsupported property '{property.Name}'.");
                }
            }
            if (id is null || version is null || source is null || source.Length is 0 or > 4096 ||
                source != source.Trim() || source.Any(char.IsControl) || !Path.GetExtension(source).Equals(".nupkg", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("settings.json nativeExtensions entries require an id, version, and local .nupkg source.");
            NativeExtensionPackageStore.ValidateId(id);
            NativeExtensionPackageStore.ValidateVersion(version);
            if (!ids.Add(id))
                throw new InvalidDataException($"settings.json nativeExtensions contains duplicate package id '{id}'.");
            packages.Add(new(id, version, source));
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
