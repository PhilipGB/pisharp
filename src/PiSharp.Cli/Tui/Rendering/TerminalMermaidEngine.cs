using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PiSharp.Cli.Tui;

/// <summary>Unicode terminal diagram for a Mermaid source block, mirroring grok-mermaid 0.2.3.</summary>
internal sealed record TerminalMermaidArt(
    IReadOnlyList<string> Plain,
    IReadOnlyList<IReadOnlyList<TerminalMermaidSpan>> Styled,
    int Width,
    IReadOnlyList<string> Warnings);

internal sealed record TerminalMermaidSpan(string Text, string Cls);

/// <summary>Process boundary for the vendored grok-mermaid renderer.</summary>
internal static class TerminalMermaidEngine
{
    internal const int MaximumSourceCharacters = 64 * 1024;
    internal const int MaximumCachedSources = 1_024;
    private const int ProcessTimeoutMilliseconds = 10_000;
    private static readonly ConcurrentDictionary<string, TerminalMermaidArt?> s_cache = new(StringComparer.Ordinal);
    private static readonly object s_cacheGate = new();
    private static string? s_wrapperPath;
    private static readonly object s_wrapperGate = new();

    internal static void ClearCacheForTests() => s_cache.Clear();

    internal static int CachedCountForTests => s_cache.Count;

    internal static bool TryGetCached(string source, out TerminalMermaidArt? art)
    {
        art = null;
        if (string.IsNullOrWhiteSpace(source))
            return false;
        if (source.Length > MaximumSourceCharacters)
            return false;
        return s_cache.TryGetValue(source, out art);
    }

    internal static void CacheForTests(string source, TerminalMermaidArt? art)
    {
        if (string.IsNullOrWhiteSpace(source))
            return;
        if (source.Length > MaximumSourceCharacters)
            return;
        CacheArt(source, art);
    }

    internal static void CacheArt(string source, TerminalMermaidArt? art)
    {
        lock (s_cacheGate)
        {
            if (s_cache.ContainsKey(source))
            {
                s_cache[source] = art;
                return;
            }
            while (s_cache.Count >= MaximumCachedSources)
            {
                string? oldest = null;
                foreach (var key in s_cache.Keys)
                {
                    oldest = key;
                    break;
                }
                if (oldest is null || !s_cache.TryRemove(oldest, out _))
                    break;
            }
            s_cache[source] = art;
        }
    }

    internal static TerminalMermaidArt? TryRender(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;
        if (source.Length > MaximumSourceCharacters)
            return null;
        if (s_cache.TryGetValue(source, out var cached))
            return cached;
        var art = RenderViaNode(source);
        CacheArt(source, art);
        return art;
    }

    private static TerminalMermaidArt? RenderViaNode(string source)
    {
        var wrapper = FindWrapper();
        if (wrapper is null)
            return null;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("node", $"\"{wrapper}\"")
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
                return null;
            process.StandardInput.Write(source);
            process.StandardInput.Close();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ProcessTimeoutMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            var output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                return null;
            var trimmed = output.Trim();
            if (trimmed.Length == 0 || trimmed == "null")
                return null;
            using var document = JsonDocument.Parse(trimmed);
            return ParseArt(document.RootElement);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or
            IOException or JsonException or OperationCanceledException or TimeoutException)
        {
            return null;
        }
    }

    private static TerminalMermaidArt? ParseArt(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Null)
            return null;
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

    private static string? FindWrapper()
    {
        lock (s_wrapperGate)
        {
            if (s_wrapperPath is not null)
                return File.Exists(s_wrapperPath) ? s_wrapperPath : null;
            foreach (var candidate in CandidateWrappers())
            {
                if (File.Exists(candidate))
                {
                    s_wrapperPath = candidate;
                    return candidate;
                }
            }
            return null;
        }
    }

    private static IEnumerable<string> CandidateWrappers()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Mermaid", "mermaid-render.mjs");
        var assemblyDirectory = Path.GetDirectoryName(typeof(TerminalMermaidEngine).Assembly.Location);
        if (assemblyDirectory is not null)
            yield return Path.Combine(assemblyDirectory, "Mermaid", "mermaid-render.mjs");
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            yield return Path.Combine(directory.FullName, "src", "PiSharp.Cli", "Tui", "Rendering", "Mermaid", "mermaid-render.mjs");
            if (directory.Name == "pisharp")
                break;
        }
    }
}
