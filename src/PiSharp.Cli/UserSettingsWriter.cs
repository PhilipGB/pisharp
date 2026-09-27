using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

/// <summary>Atomically updates one interactive setting while preserving the rest of settings.json.</summary>
internal static class UserSettingsWriter
{
    private const int MaximumSettingsBytes = 64 * 1024;
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public static async Task SetAsync(string path, string setting, string? value, bool userScope,
        CancellationToken cancellationToken = default)
    {
        var segments = setting.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ValidateValue(segments, value, userScope);
        var target = Path.GetFullPath(path);
        if (File.Exists(target)) target = new FileInfo(target).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? target;
        var directory = Path.GetDirectoryName(target)!;
        if (userScope && OperatingSystem.IsLinux())
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            Directory.CreateDirectory(directory);

        JsonObject root;
        if (File.Exists(target))
        {
            var info = new FileInfo(target);
            if (info.Length > MaximumSettingsBytes) throw new InvalidDataException("settings.json exceeds 64KB.");
            var text = await File.ReadAllTextAsync(target, cancellationToken);
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject
                ?? throw new InvalidDataException("settings.json must contain a JSON object.");
        }
        else
        {
            root = new JsonObject();
        }

        Set(root, segments, ParseValue(setting, value));
        var bytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(s_jsonOptions) + "\n");
        if (bytes.Length > MaximumSettingsBytes) throw new InvalidDataException("settings.json exceeds 64KB.");
        await ReplaceAsync(target, bytes, cancellationToken);
    }

    private static void ValidateValue(IReadOnlyList<string> segments, string? value, bool userScope)
    {
        var setting = string.Join('.', segments);
        var valid = setting switch
        {
            "hideThinkingBlock" or "quietStartup" or "enableSkillCommands" or "images.blockImages" or "compaction.enabled" or "retry.enabled" =>
                value is null or "true" or "false",
            "retry.provider.maxRetries" => value is null ||
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var retries) && retries is >= 0 and <= 20,
            "retry.provider.timeoutMs" => value is null ||
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var providerTimeout) && providerTimeout >= 0,
            "terminal.trueColor" => value is null or "auto" or "true" or "false",
            "markdown.codeBlockIndent" => true,
            "httpIdleTimeoutMs" => value is null || string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase) ||
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var idleTimeout) && idleTimeout >= 0,
            "httpProxy" when userScope => value is null || IsValidHttpProxy(value),
            "steeringMode" or "followUpMode" => value is null || PromptDeliveryModes.TryParseSettingValue(value, out _),
            "defaultProjectTrust" when userScope => value is null or "ask" or "always" or "never",
            "defaultThinkingLevel" => value is null || ThinkingLevels.IsValid(value),
            "theme" => value is null || IsValidThemeSetting(value),
            "externalEditor" => value is null || value.Length is > 0 and <= 4096 && value == value.Trim() && !value.Any(char.IsControl),
            _ => false
        };
        if (!valid) throw new ArgumentException($"'{setting}' is not an editable setting or has an invalid value.", nameof(value));
    }

    private static bool IsValidThemeSetting(string value)
    {
        if (value.Length > 128 || value.Any(char.IsControl)) return false;
        var separator = value.IndexOf('/');
        return separator switch
        {
            < 0 => !string.IsNullOrWhiteSpace(value),
            _ when value.IndexOf('/', separator + 1) >= 0 => false,
            _ => !string.IsNullOrWhiteSpace(value[..separator]) && !string.IsNullOrWhiteSpace(value[(separator + 1)..])
        };
    }

    private static bool IsValidHttpProxy(string value) => value.Length is > 0 and <= 2048 && value == value.Trim() &&
        Uri.TryCreate(value, UriKind.Absolute, out var proxy) && (proxy.Scheme is "http" or "https") &&
        !string.IsNullOrWhiteSpace(proxy.Host) && proxy.Query.Length == 0 && proxy.Fragment.Length == 0;

    private static JsonNode? ParseValue(string setting, string? value) => setting switch
    {
        _ when value is null => null,
        "hideThinkingBlock" or "quietStartup" or "enableSkillCommands" or "images.blockImages" or "compaction.enabled" or "retry.enabled" =>
            JsonValue.Create(value == "true"),
        "terminal.trueColor" when value is "true" or "false" => JsonValue.Create(value == "true"),
        "retry.provider.maxRetries" => JsonValue.Create(int.Parse(value!, NumberStyles.None, CultureInfo.InvariantCulture)),
        "retry.provider.timeoutMs" => JsonValue.Create(int.Parse(value!, NumberStyles.None, CultureInfo.InvariantCulture)),
        "httpIdleTimeoutMs" => JsonValue.Create(string.Equals(value, "disabled", StringComparison.OrdinalIgnoreCase)
            ? 0 : int.Parse(value!, NumberStyles.None, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value)
    };

    private static void Set(JsonObject root, IReadOnlyList<string> segments, JsonNode? value)
    {
        var parents = new List<(JsonObject Parent, string Key)>();
        var current = root;
        for (var index = 0; index < segments.Count - 1; index++)
        {
            var key = segments[index];
            if (current[key] is JsonObject child)
            {
                parents.Add((current, key));
                current = child;
            }
            else if (current.ContainsKey(key))
            {
                throw new InvalidDataException($"settings.json {key} must be an object.");
            }
            else
            {
                var newChild = new JsonObject();
                current[key] = newChild;
                parents.Add((current, key));
                current = newChild;
            }
        }

        var leaf = segments[^1];
        if (value is null) current.Remove(leaf);
        else current[leaf] = value;
        for (var index = parents.Count - 1; index >= 0; index--)
        {
            var (parent, key) = parents[index];
            if (parent[key] is JsonObject child && child.Count == 0) parent.Remove(key);
            else break;
        }
    }

    private static async Task ReplaceAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, ".pisharp-settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (OperatingSystem.IsLinux())
                options.UnixCreateMode = File.Exists(path) ? File.GetUnixFileMode(path) : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
