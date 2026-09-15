using System.Text.Json;

namespace PiSharp.Cli;

internal interface IChatOutput
{
    void WriteText(string text);

    void ToolStarted(string name, string arguments);

    void ToolUpdated(string name, string arguments);

    void ToolFinished(string name, string? error, string result);

    void WriteLine();
}

internal sealed class SilentChatOutput : IChatOutput
{
    public void WriteText(string text)
    {
    }

    public void ToolStarted(string name, string arguments)
    {
    }

    public void ToolUpdated(string name, string arguments)
    {
    }

    public void ToolFinished(string name, string? error, string result)
    {
    }

    public void WriteLine()
    {
    }
}

internal sealed class TerminalChatOutput : IChatOutput
{
    public void WriteText(string text) => Console.Write(text);

    public void ToolStarted(string name, string arguments) =>
        Console.WriteLine($"\n[tool:start] {name} {arguments}");

    public void ToolUpdated(string name, string arguments) =>
        Console.WriteLine($"\n[tool:update] {name} {arguments}");

    public void ToolFinished(string name, string? error, string result) =>
        Console.WriteLine($"\n[tool:end] {name}{(error is null ? string.Empty : $" error={error}")}: {result}");

    public void WriteLine() => Console.WriteLine();
}

internal sealed class JsonLineWriter
{
    private readonly TextWriter _writer;
    private readonly object _sync = new();

    public JsonLineWriter(TextWriter writer)
    {
        _writer = writer;
    }

    public void Write(object value)
    {
        var line = JsonSerializer.Serialize(value);
        lock (_sync)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }
}

internal sealed class JsonChatOutput : IChatOutput
{
    private readonly JsonLineWriter _writer;

    public JsonChatOutput(JsonLineWriter writer)
    {
        _writer = writer;
    }

    public void WriteText(string text) =>
        _writer.Write(new { type = "message_update", role = "assistant", textDelta = text });

    public void ToolStarted(string name, string arguments) =>
        _writer.Write(new { type = "toolcall_start", toolName = name, arguments });

    public void ToolUpdated(string name, string arguments) =>
        _writer.Write(new { type = "toolcall_update", toolName = name, arguments });

    public void ToolFinished(string name, string? error, string result) =>
        _writer.Write(new { type = "toolcall_end", toolName = name, error, result });

    public void WriteLine()
    {
    }
}

internal sealed record RpcCommandEnvelope(
    string? Id,
    string Type,
    string? Message,
    string? StreamingBehavior,
    string? Mode);

internal static class RpcProtocol
{
    public static RpcCommandEnvelope Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("RPC command must be an object with a string 'type'.");
        }

        return new RpcCommandEnvelope(
            GetString(root, "id"),
            type.GetString()!,
            GetString(root, "message"),
            GetString(root, "streamingBehavior"),
            GetString(root, "mode"));
    }

    public static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
