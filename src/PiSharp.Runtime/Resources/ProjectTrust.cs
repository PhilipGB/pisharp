using System.Text;
using System.Text.Json;

namespace PiSharp.Runtime.Resources;

/// <summary>Permission to load project-supplied executable/configurable resources. Not a process sandbox.</summary>
public sealed class ProjectTrust(string agentDirectory)
{
    public string PathOnDisk { get; } = Path.Combine(System.IO.Path.GetFullPath(agentDirectory), "trust.json");

    public static bool HasProtectedResources(string cwd)
    {
        var directory = System.IO.Path.GetFullPath(cwd);
        var pi = System.IO.Path.Combine(directory, ".pi");
        if (new[] { "settings.json", "extensions", "skills", "prompts", "themes", "SYSTEM.md", "APPEND_SYSTEM.md" }
            .Any(name => File.Exists(System.IO.Path.Combine(pi, name)) || Directory.Exists(System.IO.Path.Combine(pi, name)))) return true;
        var homeSkills = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
        {
            var skills = System.IO.Path.Combine(parent.FullName, ".agents", "skills");
            if (skills != homeSkills && Directory.Exists(skills)) return true;
        }
        return false;
    }

    public async Task<bool?> GetAsync(string cwd, CancellationToken cancellationToken = default)
    {
        var decisions = await ReadAsync(cancellationToken);
        for (var parent = new DirectoryInfo(System.IO.Path.GetFullPath(cwd)); parent is not null; parent = parent.Parent)
            if (decisions.TryGetValue(parent.FullName, out var value)) return value;
        return null;
    }

    public async Task SetAsync(string cwd, bool? trusted, CancellationToken cancellationToken = default)
    {
        var parent = System.IO.Path.GetDirectoryName(PathOnDisk)!;
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(parent);
        var lockOptions = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite
        };
        if (OperatingSystem.IsLinux()) lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var lease = new FileStream(PathOnDisk + ".lock", lockOptions);
        if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Trust store locking is not supported on macOS.");
        lease.Lock(0, 1);
        try
        {
            var decisions = await ReadAsync(cancellationToken);
            var key = System.IO.Path.GetFullPath(cwd);
            if (trusted is null) decisions.Remove(key);
            else decisions[key] = trusted.Value;
            var temp = PathOnDisk + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Options = FileOptions.Asynchronous
                };
                if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(temp, options))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(decisions)), cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, PathOnDisk, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { lease.Unlock(0, 1); }
    }

    public async Task<bool> ResolveAsync(string cwd, bool? overrideDecision, bool interactive,
        TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        if (overrideDecision.HasValue) return overrideDecision.Value;
        if (!HasProtectedResources(cwd)) return true;
        var saved = await GetAsync(cwd, cancellationToken);
        if (saved.HasValue) return saved.Value;
        if (!interactive) return false;
        await output.WriteLineAsync($"Trust project resources in {System.IO.Path.GetFullPath(cwd)}? [y]es / [n]o / [o]nce / [Enter] deny once");
        var answer = (await input.ReadLineAsync(cancellationToken))?.Trim().ToLowerInvariant();
        if (answer is "y" or "yes") { await SetAsync(cwd, true, cancellationToken); return true; }
        if (answer is "n" or "no") { await SetAsync(cwd, false, cancellationToken); return false; }
        return answer is "o" or "once";
    }

    private async Task<Dictionary<string, bool>> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(PathOnDisk)) return new(StringComparer.Ordinal);
        if (new FileInfo(PathOnDisk).Length > 1024 * 1024) throw new InvalidDataException("Trust store exceeds 1MB.");
        var json = await File.ReadAllTextAsync(PathOnDisk, token);
        var decisions = JsonSerializer.Deserialize<Dictionary<string, bool>>(json)
            ?? throw new InvalidDataException("Trust store must be an object.");
        if (decisions.Keys.Any(key => !System.IO.Path.IsPathFullyQualified(key)))
            throw new InvalidDataException("Trust store contains a non-absolute path.");
        return decisions;
    }
}
