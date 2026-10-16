using System.Text.Json;

namespace PiSharp.Runtime.Extensions;

internal sealed record NativeExtensionPackageManifest(
    string Id,
    string Version,
    int ApiVersion,
    IReadOnlyList<NativeExtensionPackageEntry> Extensions);

internal sealed record NativeExtensionPackageEntry(string Assembly, string Type, string AssemblyPath);

/// <summary>Reads the explicit PiSharp-native entry-point manifest from a compiled package root.</summary>
internal static class NativeExtensionPackageManifestReader
{
    internal const string FileName = "pisharp-extension.json";
    internal const int HostApiVersion = 1;

    internal static NativeExtensionPackageManifest Read(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        var root = Path.GetFullPath(packageRoot);
        if (!Directory.Exists(root))
            throw new InvalidDataException($"Native extension package directory does not exist: {root}");

        var manifestPath = Path.Combine(root, FileName);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        }
        catch (FileNotFoundException)
        {
            throw new InvalidDataException($"Native extension manifest is missing: {manifestPath}");
        }
        catch (DirectoryNotFoundException)
        {
            throw new InvalidDataException($"Native extension manifest is missing: {manifestPath}");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Native extension manifest is invalid JSON: {error.Message}", error);
        }

        using (document)
        {
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object)
                throw Invalid("The root value must be an object.");
            EnsureUniqueProperties(json, "manifest");

            var id = RequiredString(json, "id");
            if (!IsPackageId(id))
                throw Invalid("The id must contain only ASCII letters, digits, '.', '-', or '_'.");

            var version = RequiredString(json, "version");
            if (!IsValidPackageVersion(version))
                throw Invalid("The version must be a valid numeric package version with an optional prerelease or metadata suffix.");

            if (!json.TryGetProperty("apiVersion", out var apiVersionElement) ||
                apiVersionElement.ValueKind != JsonValueKind.Number || !apiVersionElement.TryGetInt32(out var apiVersion))
                throw Invalid("The apiVersion must be an integer.");
            if (apiVersion != HostApiVersion)
                throw new InvalidDataException($"Native extension package {id} requires PiSharp extension API v{apiVersion}; " +
                    $"this host supports v{HostApiVersion}.");

            if (!json.TryGetProperty("extensions", out var entriesElement) ||
                entriesElement.ValueKind != JsonValueKind.Array || entriesElement.GetArrayLength() == 0)
                throw Invalid("The extensions field must be a non-empty array.");

            var entries = new List<NativeExtensionPackageEntry>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entryElement in entriesElement.EnumerateArray())
            {
                if (entryElement.ValueKind != JsonValueKind.Object)
                    throw Invalid("Every extensions entry must be an object.");
                EnsureUniqueProperties(entryElement, "extension entry");
                var assembly = RequiredString(entryElement, "assembly");
                var type = RequiredString(entryElement, "type");
                if (!IsFullTypeName(type))
                    throw Invalid($"Extension type '{type}' must be a full type name.");

                var assemblyPath = ResolveAssemblyPath(root, assembly);
                var identity = assemblyPath + "\n" + type;
                if (!identities.Add(identity))
                    throw Invalid($"Extension entry '{assembly}' / '{type}' is duplicated.");
                entries.Add(new(assembly, type, assemblyPath));
            }

            return new(id, version, apiVersion, entries);
        }
    }

    private static string RequiredString(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw Invalid($"The {name} field must be a non-empty string.");
        return value.GetString()!.Trim();
    }

    private static void EnsureUniqueProperties(JsonElement json, string description)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in json.EnumerateObject())
            if (!seen.Add(property.Name)) throw Invalid($"The {description} contains duplicate property '{property.Name}'.");
    }

    private static bool IsPackageId(string id) => id.Length <= 128 && char.IsAsciiLetterOrDigit(id[0]) &&
        !id.Contains("..", StringComparison.Ordinal) && id.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    internal static bool IsValidPackageVersion(string version)
    {
        if (version.Length > 128 || version.Any(char.IsWhiteSpace)) return false;
        var metadataIndex = version.IndexOf('+');
        if (metadataIndex != version.LastIndexOf('+')) return false;
        var metadata = metadataIndex >= 0 ? version[(metadataIndex + 1)..] : null;
        var versionWithoutMetadata = metadataIndex >= 0 ? version[..metadataIndex] : version;
        if (metadata is not null && !IsVersionIdentifierList(metadata)) return false;

        var prereleaseIndex = versionWithoutMetadata.IndexOf('-');
        var prerelease = prereleaseIndex >= 0 ? versionWithoutMetadata[(prereleaseIndex + 1)..] : null;
        var numericVersion = prereleaseIndex >= 0 ? versionWithoutMetadata[..prereleaseIndex] : versionWithoutMetadata;
        if (prerelease is not null && !IsVersionIdentifierList(prerelease)) return false;

        var numbers = numericVersion.Split('.');
        return numbers.Length is >= 2 and <= 4 && numbers.All(number => number.Length > 0 &&
            number.All(char.IsAsciiDigit));
    }

    private static bool IsVersionIdentifierList(string value) => value.Split('.').All(segment =>
        segment.Length > 0 && segment.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));

    private static bool IsFullTypeName(string type) => type.Length <= 1024 && !type.Contains('/') &&
        !type.Contains('\\') && !type.Any(char.IsWhiteSpace) && type.Contains('.') &&
        type[0] != '.' && type[^1] != '.';

    private static string ResolveAssemblyPath(string root, string assembly)
    {
        if (assembly.Length > 1024 || Path.IsPathRooted(assembly) || assembly.StartsWith('/') ||
            assembly.StartsWith('\\') || assembly.Contains(':') ||
            !Path.GetExtension(assembly).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw Invalid($"Extension assembly path '{assembly}' must be a package-relative .dll path.");

        var segments = assembly.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw Invalid($"Extension assembly path '{assembly}' must not contain empty, '.' or '..' path segments.");

        var normalized = Path.Combine(segments);
        var fullPath = Path.GetFullPath(normalized, root);
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw Invalid($"Extension assembly path '{assembly}' escapes the package directory.");
        if (!File.Exists(fullPath))
            throw Invalid($"Extension assembly does not exist: {assembly}");
        return fullPath;
    }

    private static InvalidDataException Invalid(string message) =>
        new($"Invalid native extension manifest ({FileName}): {message}");
}
