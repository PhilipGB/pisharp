using System.Diagnostics;
using System.Text.Json;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Codemode;

/// <summary>Process boundary for one QuickJS/WASM script and its allowed host calls.</summary>
internal static class CodemodeSandbox
{
    private const int MaximumCodeCharacters = 64 * 1024;
    private const int MaximumOutputCharacters = 64 * 1024;
    private const int MaximumImageCharacters = 4 * 1024 * 1024;
    private const int MaximumCallArguments = 32 * 1024;
    private const int MaximumCalls = 128;
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    internal sealed record Result(bool Ok, string Text, string? Error,
        IReadOnlyDictionary<string, JsonElement>? Store = null,
        IReadOnlyList<PiSharpToolImage>? Images = null);

    public static async Task<Result> ExecuteAsync(string code, PiSharpToolExecutionContext context,
        IReadOnlyDictionary<string, JsonElement> store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.Length is 0 or > MaximumCodeCharacters)
            return new(false, "", $"Codemode source must be 1 to {MaximumCodeCharacters} characters.");

        var worker = Path.Combine(AppContext.BaseDirectory, "Codemode", "Engine", "worker.mjs");
        if (!File.Exists(worker)) return new(false, "", "Codemode worker assets are unavailable.");
        var tools = context.Snapshot.Callable.Where(tool => tool.Function.Name != "codemode")
            .Select(tool => new
            {
                name = tool.Function.Name,
                jsName = CodemodeToolCatalog.JavascriptIdentifier(tool.Function.Name),
                description = CodemodeToolCatalog.RenderToolSample(tool)
            }).ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("node")
            {
                ArgumentList = { "--max-old-space-size=96", worker },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        try { process.Start(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(false, "", "Codemode requires Node.js 22.19 or newer: " + error.Message);
        }
        var outstanding = new List<Task>();
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                code,
                tools,
                store,
                memoryLimitBytes = 32 * 1024 * 1024
            }, s_json));
            await process.StandardInput.FlushAsync(token);
            var output = new List<string>();
            var images = new List<PiSharpToolImage>();
            var outputLength = 0;
            var callCount = 0;
            var writeGate = new SemaphoreSlim(1, 1);
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(token);
                if (line is null) return new(false, string.Join('\n', output),
                    "Codemode worker exited before returning a result: " + (await stderr));
                if (line.Length > MaximumImageCharacters + 256)
                    return new(false, string.Join('\n', output), "Codemode output limit exceeded.");
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "output":
                        {
                            var value = root.GetProperty("value").GetString() ?? "";
                            outputLength += value.Length;
                            if (outputLength > MaximumOutputCharacters)
                                return new(false, string.Join('\n', output), "Codemode output limit exceeded.");
                            output.Add(value);
                            break;
                        }
                    case "image":
                        {
                            var data = root.GetProperty("data").GetString() ?? "";
                            var mimeType = root.GetProperty("mimeType").GetString() ?? "";
                            if (images.Count >= 4 || data.Length > MaximumImageCharacters ||
                                new PiSharpToolImage(mimeType, data).ToDataContent() is null)
                                return new(false, string.Join('\n', output), "Codemode image output limit or format was invalid.");
                            images.Add(new PiSharpToolImage(mimeType, data));
                            break;
                        }
                    case "call":
                    case "global":
                        {
                            if (++callCount > MaximumCalls)
                                return new(false, string.Join('\n', output), "Codemode call limit exceeded.");
                            var id = root.GetProperty("id").GetInt32();
                            var name = root.GetProperty("name").GetString() ?? "";
                            var args = root.GetProperty("args").GetString();
                            if (args?.Length > MaximumCallArguments)
                                return new(false, string.Join('\n', output), "Codemode call arguments exceed the limit.");
                            outstanding.Add(DispatchAsync(id, name, args, root.GetProperty("type").GetString() == "global",
                                context, process, writeGate, token));
                            break;
                        }
                    case "done":
                        {
                            var ok = root.GetProperty("ok").GetBoolean();
                            var value = root.GetProperty("value").GetString();
                            if (ok && value is not null)
                            {
                                using var returned = JsonDocument.Parse(value);
                                var rendered = returned.RootElement.ValueKind == JsonValueKind.String
                                    ? returned.RootElement.GetString() ?? "" : returned.RootElement.GetRawText();
                                outputLength += rendered.Length;
                                if (outputLength > MaximumOutputCharacters)
                                    return new(false, string.Join('\n', output), "Codemode output limit exceeded.");
                                output.Add(rendered);
                            }
                            var changed = ok ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                                root.GetProperty("store").GetString() ?? "{}", s_json) : null;
                            return new(ok, string.Join('\n', output), ok ? null : value, changed, images);
                        }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new(false, "", cancellationToken.IsCancellationRequested
                ? "Codemode was cancelled." : "Codemode timed out after 30 seconds.");
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException)
        {
            return new(false, "", "Codemode worker protocol error: " + error.Message);
        }
        finally
        {
            deadline.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(outstanding).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException or InvalidOperationException) { }
        }
    }

    private static async Task DispatchAsync(int id, string name, string? args, bool isGlobal,
        PiSharpToolExecutionContext context,
        Process process, SemaphoreSlim writeGate, CancellationToken cancellationToken)
    {
        bool ok;
        string? payload;
        try
        {
            if (isGlobal)
            {
                if (name is not ("searchTools" or "describeTool"))
                    throw new ArgumentException("Unknown Codemode global.");
                using var parsed = JsonDocument.Parse(args ?? "null");
                var input = parsed.RootElement;
                var query = name == "searchTools"
                    ? input.ValueKind == JsonValueKind.Array && input.GetArrayLength() > 0 &&
                      input[0].ValueKind == JsonValueKind.String ? input[0].GetString() : null
                    : input.ValueKind == JsonValueKind.String ? input.GetString() : null;
                if (string.IsNullOrWhiteSpace(query) || query.Length > 512)
                    throw new ArgumentException("A name or query of at most 512 characters is required.");
                var limit = 8;
                string? toolNamespace = null;
                if (name == "searchTools" && input.GetArrayLength() > 1 && input[1].ValueKind != JsonValueKind.Null)
                {
                    if (input[1].ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("Search options must be an object.");
                    if (input[1].TryGetProperty("limit", out var limitValue) &&
                        (!limitValue.TryGetInt32(out limit) || limit is < 1 or > 16))
                        throw new ArgumentException("Search limit must be between 1 and 16.");
                    if (input[1].TryGetProperty("namespace", out var namespaceValue))
                    {
                        if (namespaceValue.ValueKind != JsonValueKind.String)
                            throw new ArgumentException("Search namespace must be a string.");
                        toolNamespace = namespaceValue.GetString();
                    }
                }
                var candidates = context.Snapshot.Callable.Where(tool => tool.Function.Name != "codemode")
                    .Where(tool => toolNamespace is null || tool.Namespace?.Name == toolNamespace)
                    .Take(2000).ToArray();
                ok = true;
                payload = name == "searchTools"
                    ? JsonSerializer.Serialize(ToolSearchRanker.Rank(query, candidates, limit, cancellationToken)
                        .Select(tool => new
                        {
                            name = CodemodeToolCatalog.JavascriptIdentifier(tool.Function.Name),
                            description = CodemodeToolCatalog.RenderToolSample(tool)
                        }).ToArray(), s_json)
                    : candidates.Where(tool => tool.Function.Name == query ||
                            CodemodeToolCatalog.JavascriptIdentifier(tool.Function.Name) == query)
                        .Select(CodemodeToolCatalog.RenderToolSample).FirstOrDefault() is { } sample
                            ? JsonSerializer.Serialize(sample, s_json) : null;
            }
            else
            {
                IReadOnlyDictionary<string, object?> arguments = new Dictionary<string, object?>();
                if (args is not null)
                {
                    using var parsed = JsonDocument.Parse(args);
                    if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("Tool arguments must be a JSON object.");
                    arguments = parsed.RootElement.EnumerateObject().ToDictionary(
                        property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
                }
                var result = await context.ExecuteToolAsync(name, arguments, cancellationToken);
                var hasOutputSchema = context.Snapshot.Callable.Any(tool => tool.Function.Name == name &&
                    tool.OutputSchema is not null);
                if (hasOutputSchema && result.StructuredContent is { } structured)
                {
                    ok = true;
                    payload = structured.GetRawText();
                }
                else
                {
                    ok = !result.IsError;
                    payload = ok ? JsonSerializer.Serialize(result.Text, s_json) : result.Error ?? result.Text;
                }
            }
            if (payload is { Length: > 4 * 1024 * 1024 })
            {
                ok = false;
                payload = "Nested tool result exceeds the Codemode transfer limit.";
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ok = false;
            payload = error.Message;
        }
        if (cancellationToken.IsCancellationRequested) return;
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, ok, payload }, s_json));
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { writeGate.Release(); }
    }

}
