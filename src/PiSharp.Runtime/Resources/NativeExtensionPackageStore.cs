using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Resources;

/// <summary>A configured local package source for a PiSharp-native compiled extension.</summary>
public sealed record NativeExtensionPackageSource(string Id, string Version, string Source);

/// <summary>An installed native extension package root selected from settings.</summary>
public sealed record NativeExtensionPackageRoot(string Source, string Scope, string Path);

/// <summary>Installs deterministic local .nupkg files into immutable, versioned extension roots.</summary>
public sealed class NativeExtensionPackageStore
{
    private const long MaximumPackageBytes = 256 * 1024 * 1024;
    private const int MaximumPackageEntries = 10_000;
    private const int MaximumManifestBytes = 64 * 1024;
    private const string ManifestName = "pisharp-extension.json";

    private readonly string _agentDirectory;

    public NativeExtensionPackageStore(string agentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDirectory);
        _agentDirectory = Path.GetFullPath(agentDirectory);
    }

    /// <summary>Validates and installs a local compiled package before its settings entry is activated.</summary>
    public async Task<NativeExtensionPackageSource> InstallAsync(string workingDirectory, string source, string scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ValidateScope(scope);
        if (!Path.GetExtension(source).Equals(".nupkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PiSharp-native extensions must be installed from a local .nupkg file.");

        var cwd = Path.GetFullPath(workingDirectory);
        var sourcePath = Path.GetFullPath(source, cwd);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Native extension package not found.", sourcePath);
        var packageBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        if (packageBytes.LongLength > MaximumPackageBytes)
            throw new InvalidDataException("Native extension package exceeds the 256 MB limit.");
        var packageHash = Convert.ToHexString(SHA256.HashData(packageBytes));

        var scopeRoot = GetScopeRoot(cwd, scope);
        Directory.CreateDirectory(scopeRoot);
        var stagingRoot = Path.Combine(scopeRoot, ".install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            await ExtractPackageAsync(packageBytes, stagingRoot, cancellationToken);
            var manifest = await ValidatePackageAsync(stagingRoot, cancellationToken);
            var finalRoot = GetPackageRoot(cwd, scope, manifest.Id, manifest.Version);
            Directory.CreateDirectory(Path.GetDirectoryName(finalRoot)!);
            var fingerprintPath = Path.Combine(stagingRoot, ".pisharp-package.sha256");
            await File.WriteAllTextAsync(fingerprintPath, packageHash + "\n", cancellationToken);

            if (Directory.Exists(finalRoot))
            {
                var existingFingerprintPath = Path.Combine(finalRoot, ".pisharp-package.sha256");
                var existingFingerprint = File.Exists(existingFingerprintPath)
                    ? (await File.ReadAllTextAsync(existingFingerprintPath, cancellationToken)).Trim()
                    : null;
                if (!string.Equals(existingFingerprint, packageHash, StringComparison.Ordinal))
                    throw new InvalidDataException($"Native extension {manifest.Id} {manifest.Version} is already installed with different contents. Use a new package version.");
                return new(manifest.Id, manifest.Version, sourcePath);
            }

            try
            {
                Directory.Move(stagingRoot, finalRoot);
            }
            catch (IOException) when (Directory.Exists(finalRoot))
            {
                var existingFingerprintPath = Path.Combine(finalRoot, ".pisharp-package.sha256");
                var existingFingerprint = File.Exists(existingFingerprintPath)
                    ? (await File.ReadAllTextAsync(existingFingerprintPath, cancellationToken)).Trim()
                    : null;
                if (!string.Equals(existingFingerprint, packageHash, StringComparison.Ordinal)) throw;
            }
            return new(manifest.Id, manifest.Version, sourcePath);
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
    }

    /// <summary>Reads and validates a local package manifest without activating or persisting the package.</summary>
    public async Task<NativeExtensionPackageSource> InspectAsync(string workingDirectory, string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!Path.GetExtension(source).Equals(".nupkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PiSharp-native extensions must be provided as a local .nupkg file.");
        var sourcePath = Path.GetFullPath(source, Path.GetFullPath(workingDirectory));
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Native extension package not found.", sourcePath);
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        if (bytes.LongLength > MaximumPackageBytes)
            throw new InvalidDataException("Native extension package exceeds the 256 MB limit.");

        var inspectionRoot = Path.Combine(Path.GetTempPath(), "pisharp-native-package-inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inspectionRoot);
        try
        {
            await ExtractPackageAsync(bytes, inspectionRoot, cancellationToken);
            var manifest = await ValidatePackageAsync(inspectionRoot, cancellationToken);
            return new(manifest.Id, manifest.Version, sourcePath);
        }
        finally
        {
            if (Directory.Exists(inspectionRoot)) Directory.Delete(inspectionRoot, recursive: true);
        }
    }

    /// <summary>Resolves configured user and project packages to persistent roots without executing their code.</summary>
    public Task<IReadOnlyList<NativeExtensionPackageRoot>> ResolveAsync(string workingDirectory, bool trusted,
        IReadOnlyList<NativeExtensionPackageSource>? userSources = null,
        IReadOnlyList<NativeExtensionPackageSource>? projectSources = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = trusted; // Trust gates execution in the catalog; project roots remain inspectable while untrusted.
        var cwd = Path.GetFullPath(workingDirectory);
        var roots = new List<NativeExtensionPackageRoot>();
        AddRoots(userSources ?? [], "user");
        AddRoots(projectSources ?? [], "project");
        return Task.FromResult<IReadOnlyList<NativeExtensionPackageRoot>>(roots);

        void AddRoots(IReadOnlyList<NativeExtensionPackageSource> sources, string scope)
        {
            foreach (var package in sources)
            {
                ValidatePackageReference(package);
                var packageRoot = GetPackageRoot(cwd, scope, package.Id, package.Version);
                roots.Add(new(package.Source, scope, packageRoot));
            }
        }
    }

    /// <summary>Removes all installed versions for a package ID in one scope.</summary>
    public Task RemoveAsync(string workingDirectory, string id, string scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateScope(scope);
        ValidateId(id);
        var packageDirectory = Path.Combine(GetScopeRoot(Path.GetFullPath(workingDirectory), scope), id.ToLowerInvariant());
        if (Directory.Exists(packageDirectory)) Directory.Delete(packageDirectory, recursive: true);
        return Task.CompletedTask;
    }

    private string GetScopeRoot(string workingDirectory, string scope) => Path.Combine(
        scope == "project" ? Path.Combine(workingDirectory, ".pi") : _agentDirectory, "native-extensions");

    private string GetPackageRoot(string workingDirectory, string scope, string id, string version) => Path.Combine(
        GetScopeRoot(workingDirectory, scope), id.ToLowerInvariant(), version);

    private static async Task ExtractPackageAsync(byte[] bytes, string stagingRoot, CancellationToken cancellationToken)
    {
        using var archiveStream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > MaximumPackageEntries)
            throw new InvalidDataException($"Native extension package must contain between 1 and {MaximumPackageEntries} entries.");

        long uncompressedBytes = 0;
        var root = Path.GetFullPath(stagingRoot) + Path.DirectorySeparatorChar;
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.Length == 0 || entry.FullName.Contains('\\') || entry.FullName.Contains(':') ||
                entry.FullName.StartsWith("/", StringComparison.Ordinal) ||
                Path.IsPathRooted(entry.FullName) || entry.FullName.Split('/').Any(segment => segment is ".." or "."))
                throw new InvalidDataException($"Native extension package contains an unsafe path: '{entry.FullName}'.");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException($"Native extension package contains a symbolic link: '{entry.FullName}'.");

            var relativePath = entry.FullName.TrimEnd('/');
            if (relativePath.Length == 0) continue;
            var destination = Path.GetFullPath(Path.Combine(stagingRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException($"Native extension package contains an unsafe path: '{entry.FullName}'.");
            if (!paths.Add(destination)) throw new InvalidDataException($"Native extension package contains a duplicate path: '{entry.FullName}'.");
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            uncompressedBytes = checked(uncompressedBytes + entry.Length);
            if (uncompressedBytes > MaximumPackageBytes)
                throw new InvalidDataException("Native extension package expands beyond the 256 MB limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task<PackageManifestHeader> ValidatePackageAsync(string packageRoot, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(packageRoot, ManifestName);
        if (!File.Exists(manifestPath)) throw new InvalidDataException($"Native extension package is missing root {ManifestName}.");
        var info = new FileInfo(manifestPath);
        if (info.Length is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException($"Native extension {ManifestName} must be between 1 and {MaximumManifestBytes} bytes.");
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken),
            new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Native extension {ManifestName} must contain a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!seen.Add(property.Name)) throw new InvalidDataException($"Native extension manifest has duplicate property '{property.Name}'.");

        var root = document.RootElement;
        var id = RequiredString(root, "id");
        var version = RequiredString(root, "version");
        ValidateId(id);
        ValidateVersion(version);
        if (!root.TryGetProperty("apiVersion", out var apiVersion) || apiVersion.ValueKind != JsonValueKind.Number ||
            !apiVersion.TryGetInt32(out var apiVersionValue))
            throw new InvalidDataException("Native extension manifest apiVersion must be an integer.");
        if (apiVersionValue != 1)
            throw new InvalidDataException($"Native extension package {id} requires PiSharp extension API v{apiVersionValue}; this host supports v1.");
        if (!root.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != JsonValueKind.Array ||
            extensions.GetArrayLength() is 0 or > 64)
            throw new InvalidDataException("Native extension manifest extensions must contain between 1 and 64 entries.");

        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in extensions.EnumerateArray())
        {
            if (extension.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Native extension manifest entries must be objects.");
            var assembly = RequiredString(extension, "assembly");
            var type = RequiredString(extension, "type");
            var assemblySegments = assembly.Split('/');
            if (assembly.Length > 1024 || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                assembly.Contains('\\') || assembly.Contains(':') || assembly.StartsWith("/", StringComparison.Ordinal) ||
                Path.IsPathRooted(assembly) || assemblySegments.Any(segment => segment.Length == 0 || segment is ".." or ".") ||
                type.Length > 1024 || !type.Contains('.') || type[0] == '.' || type[^1] == '.' ||
                type.Contains('/') || type.Contains('\\') || type.Any(char.IsWhiteSpace) || type.Any(char.IsControl))
                throw new InvalidDataException("Native extension manifest entry requires a safe relative assembly DLL path and full type name.");
            if (!entries.Add(assembly + "\n" + type))
                throw new InvalidDataException($"Native extension manifest entry '{assembly}' / '{type}' is duplicated.");
            var assemblyPath = Path.GetFullPath(Path.Combine(packageRoot, assembly.Replace('/', Path.DirectorySeparatorChar)));
            var packagePrefix = Path.GetFullPath(packageRoot) + Path.DirectorySeparatorChar;
            if (!assemblyPath.StartsWith(packagePrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !File.Exists(assemblyPath))
                throw new InvalidDataException($"Native extension manifest assembly is missing: '{assembly}'.");
        }
        return new(id, version);
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { Length: > 0 } value || value != value.Trim() || value.Any(char.IsControl))
            throw new InvalidDataException($"Native extension manifest requires a non-empty '{name}' string.");
        return value;
    }

    private static void ValidatePackageReference(NativeExtensionPackageSource package)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidateId(package.Id);
        ValidateVersion(package.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(package.Source);
    }

    private static void ValidateScope(string scope)
    {
        if (scope is not ("user" or "project")) throw new ArgumentException("Package scope must be 'user' or 'project'.", nameof(scope));
    }

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id != id.Trim() ||
            !char.IsAsciiLetterOrDigit(id[0]) || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_')) ||
            id.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid native extension package id '{id}'.");
    }

    internal static void ValidateVersion(string version)
    {
        if (!NativeExtensionPackageManifestReader.IsValidPackageVersion(version))
            throw new InvalidDataException($"Invalid native extension package version '{version}'.");
    }

    private sealed record PackageManifestHeader(string Id, string Version);
}
