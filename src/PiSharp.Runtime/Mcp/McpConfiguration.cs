using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Mcp;

public enum McpToolExposure { Codemode, CodemodeDeferred, Deferred, Direct, Hidden }

public sealed record McpServerConfiguration(
    string Name,
    string SourcePath,
    string Scope,
    bool Enabled,
    McpToolExposure Exposure,
    IReadOnlyDictionary<string, McpToolExposure> ToolExposure,
    TimeSpan Timeout,
    string? Command,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    Uri? Url,
    IReadOnlyDictionary<string, string> Headers,
    JsonElement? OAuth)
{
    public McpToolExposure ExposureFor(string toolName)
    {
        if (ToolExposure.TryGetValue(toolName, out var exact)) return exact;
        foreach (var (pattern, exposure) in ToolExposure)
            if (pattern.Contains('*') && WildcardMatches(pattern, toolName)) return exposure;
        return Exposure;
    }

    private static bool WildcardMatches(string pattern, string value)
    {
        var expression = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(value, expression, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }
}

public sealed record McpConfiguration(IReadOnlyList<McpServerConfiguration> Servers,
    bool AutoEnableCodemode, IReadOnlyList<string> Errors)
{
    private const int MaximumConfigBytes = 1024 * 1024;
    private static readonly Regex s_serverName = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);

    public static async Task<McpConfiguration> LoadAsync(string agentDirectory, string workingDirectory,
        bool projectTrusted, CancellationToken cancellationToken = default)
    {
        var servers = new Dictionary<string, McpServerConfiguration>(StringComparer.Ordinal);
        var errors = new List<string>();
        var autoEnable = true;
        await ReadAsync(Path.Combine(agentDirectory, "mcp.json"), "global");
        if (projectTrusted) await ReadAsync(Path.Combine(workingDirectory, ".pi", "mcp.json"), "project");
        return new(servers.Values.ToArray(), autoEnable, errors);

        async Task ReadAsync(string path, string scope)
        {
            if (!File.Exists(path)) return;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaximumConfigBytes) throw new InvalidDataException("MCP config exceeds 1 MiB.");
                var json = await File.ReadAllBytesAsync(path, cancellationToken);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    root.TryGetProperty("mcpServers", out var listed) && listed.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Expected an object with an mcpServers object.");
                if (root.TryGetProperty("autoEnableCodemode", out var auto))
                {
                    if (auto.ValueKind is JsonValueKind.True or JsonValueKind.False) autoEnable = auto.GetBoolean();
                    else errors.Add(path + ": autoEnableCodemode must be boolean.");
                }
                if (!root.TryGetProperty("mcpServers", out listed)) return;
                foreach (var entry in listed.EnumerateObject())
                    try { servers[entry.Name] = Parse(entry.Name, entry.Value, path, scope); }
                    catch (ArgumentException error) { errors.Add(path + ": " + error.Message); }
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                errors.Add(path + ": " + error.Message);
            }
        }
    }

    internal static McpServerConfiguration Parse(string name, JsonElement value, string path, string scope)
    {
        if (!s_serverName.IsMatch(name)) throw new ArgumentException($"Invalid MCP server name: {name}");
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException($"MCP server {name} must be an object.");
        var type = String(value, "type");
        var command = String(value, "command");
        var urlText = String(value, "url");
        if (command is not null == (urlText is not null))
            throw new ArgumentException($"MCP server {name} needs exactly one of command or url.");
        if (command is not null && type is not (null or "stdio") ||
            urlText is not null && type is not (null or "http" or "streamable-http"))
            throw new ArgumentException($"MCP server {name} has an incompatible transport type.");
        Uri? url = null;
        if (urlText is not null && (!Uri.TryCreate(urlText, UriKind.Absolute, out url) ||
            url.Scheme is not ("http" or "https")))
            throw new ArgumentException($"MCP server {name} url must be http or https.");
        var exposure = ParseExposure(String(value, "exposure"), name);
        var overrides = new Dictionary<string, McpToolExposure>(StringComparer.Ordinal);
        if (value.TryGetProperty("toolExposure", out var perTool))
        {
            if (perTool.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"MCP server {name} toolExposure must be an object.");
            foreach (var item in perTool.EnumerateObject())
                overrides.Add(item.Name, ParseExposure(item.Value.ValueKind == JsonValueKind.String
                    ? item.Value.GetString() : "invalid", name));
        }
        var enabled = Boolean(value, "enabled") ?? true;
        var timeout = Number(value, "timeout") ?? 60;
        if (timeout is <= 0 or > 3600) throw new ArgumentException($"MCP server {name} timeout must be 1–3600 seconds.");
        var arguments = StringArray(value, "args");
        var environment = StringMap(value, "env");
        var headers = StringMap(value, "headers");
        var workingDirectory = String(value, "cwd");
        if (url is not null && (arguments.Count > 0 || environment.Count > 0 || workingDirectory is not null) ||
            command is not null && headers.Count > 0)
            throw new ArgumentException($"MCP server {name} mixes stdio and HTTP options.");
        var oauth = value.TryGetProperty("oauth", out var auth)
            ? auth.ValueKind == JsonValueKind.Object ? auth.Clone()
                : throw new ArgumentException($"MCP server {name} oauth must be an object.")
            : (JsonElement?)null;
        return new(name, path, scope, enabled, exposure, overrides, TimeSpan.FromSeconds(timeout), command,
            arguments, workingDirectory, environment, url, headers, oauth);
    }

    private static McpToolExposure ParseExposure(string? value, string name) => value switch
    {
        null or "codemode" => McpToolExposure.Codemode,
        "codemode-deferred" => McpToolExposure.CodemodeDeferred,
        "deferred" => McpToolExposure.Deferred,
        "direct" => McpToolExposure.Direct,
        "hidden" => McpToolExposure.Hidden,
        _ => throw new ArgumentException($"MCP server {name} has invalid exposure.")
    };

    private static string? String(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found)) return null;
        if (found.ValueKind != JsonValueKind.String) throw new ArgumentException($"MCP {property} must be a string.");
        return found.GetString();
    }

    private static bool? Boolean(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found)) return null;
        if (found.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException($"MCP {property} must be boolean.");
        return found.GetBoolean();
    }

    private static double? Number(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found)) return null;
        if (found.ValueKind != JsonValueKind.Number || !found.TryGetDouble(out var number) ||
            !double.IsFinite(number)) throw new ArgumentException($"MCP {property} must be a finite number.");
        return number;
    }

    private static IReadOnlyList<string> StringArray(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found)) return [];
        if (found.ValueKind != JsonValueKind.Array || found.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            throw new ArgumentException($"MCP {property} must be an array of strings.");
        return found.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }

    private static IReadOnlyDictionary<string, string> StringMap(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found)) return new Dictionary<string, string>();
        if (found.ValueKind != JsonValueKind.Object || found.EnumerateObject().Any(item => item.Value.ValueKind != JsonValueKind.String))
            throw new ArgumentException($"MCP {property} must map names to strings.");
        return found.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!, StringComparer.Ordinal);
    }
}
