using System.Security;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Runtime.Resources;

using var inputs = JsonDocument.Parse(File.ReadAllText(args[0]));
var results = new List<object>();
foreach (var scenario in inputs.RootElement.EnumerateArray())
{
    var root = Path.Combine(args[1], "native", scenario.GetProperty("id").GetString()!);
    Directory.CreateDirectory(root);
    foreach (var file in scenario.GetProperty("files").EnumerateObject())
    {
        var path = Path.Combine(root, file.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (file.Value.TryGetProperty("fifo", out var fifo) && fifo.GetBoolean())
        {
            using var process = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { path } })!;
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("Could not create instruction FIFO fixture.");
            continue;
        }
        var bytes = Convert.FromBase64String(file.Value.GetProperty("bytes").GetString()!);
        if (file.Value.TryGetProperty("gitDirectory", out _))
            bytes = Encoding.UTF8.GetBytes("gitdir: " + Path.Combine(root, file.Value.GetProperty("gitDirectory").GetString()!) + "\n");
        await File.WriteAllBytesAsync(path, bytes);
        if (!OperatingSystem.IsWindows() && file.Value.TryGetProperty("denied", out var denied) && denied.GetBoolean())
            File.SetUnixFileMode(path, UnixFileMode.None);
    }
    var cwd = Path.Combine(root, scenario.GetProperty("cwd").GetString()!);
    var agent = Path.Combine(root, scenario.GetProperty("agent").GetString()!);
    Directory.CreateDirectory(cwd);
    Directory.CreateDirectory(agent);
    var loaded = new List<string>();
    var warnings = new List<string>();
    string? error = null;
    string? hash = null;
    try
    {
        var text = await ContextInstructions.LoadAsync(cwd, agent, loadedFiles: loaded,
            reportDiagnostic: diagnostic => warnings.Add(Path.GetRelativePath(root, diagnostic.Path!).Replace('\\', '/')));
        text = text.Replace(SecurityElement.Escape(root)!, "<workspace>", StringComparison.Ordinal);
        hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    catch (Exception caught) when (caught is IOException or UnauthorizedAccessException or InvalidDataException)
    {
        error = caught.GetType().Name;
    }
    results.Add(new { id = scenario.GetProperty("id").GetString(), paths = loaded.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/')),
        hash, error, warnings });
}
Console.WriteLine(JsonSerializer.Serialize(results));
