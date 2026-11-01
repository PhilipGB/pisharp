using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PiSharp.Cli.Tui;

/// <summary>Persistent Node worker for Mermaid rendering. Single process, serialized requests.</summary>
internal static class TerminalMermaidWorker
{
    private static readonly SemaphoreSlim s_gate = new(1, 1);
    private static readonly object s_processGate = new();
    private static Process? s_process;
    private static StreamWriter? s_writer;
    private static StreamReader? s_reader;
    private static long s_nextId;
    private static int s_processCreationCount;
    private static string? s_workerPath;

    internal static int ProcessCreationCount => Volatile.Read(ref s_processCreationCount);

    internal static void ResetForTests()
    {
        lock (s_processGate)
        {
            try { s_process?.Kill(entireProcessTree: true); } catch { }
            try { s_process?.Dispose(); } catch { }
            s_process = null;
            s_writer = null;
            s_reader = null;
            s_workerPath = null;
        }
        Interlocked.Exchange(ref s_processCreationCount, 0);
        Interlocked.Exchange(ref s_nextId, 0);
    }

    internal static async Task<TerminalMermaidArt?> RenderAsync(string source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        await s_gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!EnsureWorker())
                return null;
            var id = Interlocked.Increment(ref s_nextId);
            var request = JsonSerializer.Serialize(new { id, source });
            await s_writer!.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
            await s_writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            string? line;
            while (true)
            {
                line = await s_reader!.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    RestartWorker();
                    return null;
                }
                if (line.Length == 0)
                    continue;
                break;
            }
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var responseId) || responseId != id)
                return null;
            if (!root.TryGetProperty("art", out var artElement) || artElement.ValueKind == JsonValueKind.Null)
                return null;
            return ParseArt(artElement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or TimeoutException)
        {
            return null;
        }
        finally
        {
            s_gate.Release();
        }
    }

    private static bool EnsureWorker()
    {
        lock (s_processGate)
        {
            if (s_process is not null && !s_process.HasExited && s_writer is not null && s_reader is not null)
                return true;
            try { s_process?.Dispose(); } catch { }
            s_process = null;
            s_writer = null;
            s_reader = null;
            var worker = FindWorker();
            if (worker is null)
                return false;
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo("node", $"\"{worker}\"")
                    {
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        StandardOutputEncoding = Encoding.UTF8,
                    }
                };
                if (!process.Start())
                    return false;
                s_process = process;
                s_writer = process.StandardInput;
                s_reader = process.StandardOutput;
                _ = process.StandardError.ReadToEndAsync();
                Interlocked.Increment(ref s_processCreationCount);
                return true;
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return false;
            }
        }
    }

    private static void RestartWorker()
    {
        lock (s_processGate)
        {
            try { s_process?.Kill(entireProcessTree: true); } catch { }
            try { s_process?.Dispose(); } catch { }
            s_process = null;
            s_writer = null;
            s_reader = null;
        }
    }

    private static TerminalMermaidArt? ParseArt(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (!root.TryGetProperty("plain", out var plainElement) || plainElement.ValueKind != JsonValueKind.Array)
            return null;
        if (!root.TryGetProperty("styled", out var styledElement) || styledElement.ValueKind != JsonValueKind.Array)
            return null;
        if (!root.TryGetProperty("width", out var widthElement) || !widthElement.TryGetInt32(out var width))
            return null;
        var plain = new List<string>();
        foreach (var line in plainElement.EnumerateArray())
            plain.Add(line.ValueKind == JsonValueKind.String ? line.GetString() ?? "" : "");
        var styled = new List<IReadOnlyList<TerminalMermaidSpan>>();
        foreach (var row in styledElement.EnumerateArray())
        {
            var spans = new List<TerminalMermaidSpan>();
            if (row.ValueKind == JsonValueKind.Array)
            {
                foreach (var span in row.EnumerateArray())
                {
                    if (span.ValueKind != JsonValueKind.Object)
                        continue;
                    var text = span.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                        ? textElement.GetString() ?? "" : "";
                    var cls = span.TryGetProperty("cls", out var clsElement) && clsElement.ValueKind == JsonValueKind.String
                        ? clsElement.GetString() ?? "none" : "none";
                    spans.Add(new TerminalMermaidSpan(text, cls));
                }
            }
            styled.Add(spans);
        }
        var warnings = new List<string>();
        if (root.TryGetProperty("warnings", out var warningsElement) && warningsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var warning in warningsElement.EnumerateArray())
                if (warning.ValueKind == JsonValueKind.String && warning.GetString() is { } text)
                    warnings.Add(text);
        }
        return new TerminalMermaidArt(plain, styled, width, warnings);
    }

    private static string? FindWorker()
    {
        lock (s_processGate)
        {
            if (s_workerPath is not null)
                return File.Exists(s_workerPath) ? s_workerPath : null;
            foreach (var candidate in CandidateWorkers())
            {
                if (File.Exists(candidate))
                {
                    s_workerPath = candidate;
                    return candidate;
                }
            }
            return null;
        }
    }

    private static IEnumerable<string> CandidateWorkers()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Mermaid", "mermaid-worker.mjs");
        var assemblyDirectory = Path.GetDirectoryName(typeof(TerminalMermaidWorker).Assembly.Location);
        if (assemblyDirectory is not null)
            yield return Path.Combine(assemblyDirectory, "Mermaid", "mermaid-worker.mjs");
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            yield return Path.Combine(directory.FullName, "src", "PiSharp.Cli", "Tui", "Rendering", "Mermaid", "mermaid-worker.mjs");
            if (directory.Name == "pisharp")
                break;
        }
    }
}
