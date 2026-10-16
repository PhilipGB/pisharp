using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Cli;
using PiSharp.Runtime.Resources;

using var inputs = JsonDocument.Parse(File.ReadAllText(args[0]));
var results = new List<object>();
foreach (var scenario in inputs.RootElement.EnumerateArray())
{
    var id = scenario.GetProperty("id").GetString()!;
    var root = Path.Combine(args[1], "native", id);
    var cwd = Path.Combine(root, "project");
    var agent = Path.Combine(root, "agent");
    Directory.CreateDirectory(Path.Combine(cwd, ".pi"));
    Directory.CreateDirectory(agent);
    foreach (var file in scenario.GetProperty("files").EnumerateObject())
    {
        var path = Path.Combine(root, file.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (file.Value.TryGetProperty("directory", out var directory) && directory.GetBoolean())
        { Directory.CreateDirectory(path); continue; }
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(file.Value.GetProperty("bytes").GetString()!));
        if (!OperatingSystem.IsWindows() && file.Value.TryGetProperty("denied", out var denied) && denied.GetBoolean())
            File.SetUnixFileMode(path, UnixFileMode.None);
    }
    object? result = null;
    string? error = null;
    var warnings = new List<string>();
    void Warn(ResourceDiagnostic diagnostic)
    {
        var match = Regex.Match(diagnostic.Message, "^(Warning: Could not read .+? file .+?): ");
        if (!match.Success) throw new InvalidOperationException("Unexpected prompt warning.");
        warnings.Add(match.Groups[1].Value.Replace(root, "<workspace>", StringComparison.Ordinal));
    }
    try
    {
        var arguments = new List<string>();
        if (scenario.TryGetProperty("system", out var system)) arguments.AddRange(["--system-prompt", system.GetString()!]);
        if (scenario.TryGetProperty("append", out var append))
            foreach (var item in append.EnumerateArray()) arguments.AddRange(["--append-system-prompt", item.GetString()!]);
        var cli = CliArguments.Parse(arguments.ToArray());
        var resolved = await ProjectPrompts.LoadWithSourcesAsync(cwd, agent, scenario.GetProperty("trusted").GetBoolean(),
            reportDiagnostic: Warn, systemPromptOverride: cli.SystemPrompt, appendSystemPromptOverrides: cli.AppendSystemPrompts);
        result = new { system = Summary(resolved.System, root), append = Summary(resolved.Append ?? "", root),
            systemPath = resolved.SystemPath is { } path ? Path.GetRelativePath(root, path) : null,
            appendPaths = resolved.AppendPaths.Select(path => Path.GetRelativePath(root, path)).ToArray() };
    }
    catch (Exception caught) when (caught is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
    { error = caught.GetType().Name; }
    results.Add(new { id, result, error, warnings });
}
Console.WriteLine(JsonSerializer.Serialize(results));
static object? Summary(string? text, string root)
{
    if (text is null) return null;
    text = text.Replace(root, "<workspace>", StringComparison.Ordinal);
    return new { hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), characters = text.Length };
}
