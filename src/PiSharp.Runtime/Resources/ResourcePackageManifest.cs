using System.Text.Json;

namespace PiSharp.Runtime.Resources;

internal static class ResourcePackageManifest
{
    internal static Dictionary<string, IReadOnlyList<string>>? Read(string root)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("pi", out var pi) || pi.ValueKind != JsonValueKind.Object) return null;
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var kind in new[] { "skills", "prompts", "themes", "extensions" })
                if (pi.TryGetProperty(kind, out var entries) && entries.ValueKind == JsonValueKind.Array &&
                    entries.EnumerateArray().All(entry => entry.ValueKind == JsonValueKind.String))
                    result[kind] = entries.EnumerateArray().Select(entry => entry.GetString()!).ToArray();
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
