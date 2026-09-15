using System.Text.Json;

namespace PiSharp.Cli;

internal interface IChatOutput
{
    void AgentStarted();

    void AgentFinished(string assistantText, bool cancelled);

    void AssistantMessageStarted();

    void AssistantMessageFinished(string assistantText);

    void WriteText(string text);

    void ToolStarted(string callId, string name, string arguments);

    void ToolUpdated(string callId, string name, string arguments);

    void ToolFinished(string callId, string name, string? error, string result);

    void WriteLine();
}

internal sealed class SilentChatOutput : IChatOutput
{
    public void AgentStarted()
    {
    }

    public void AgentFinished(string assistantText, bool cancelled)
    {
    }

    public void AssistantMessageStarted()
    {
    }

    public void AssistantMessageFinished(string assistantText)
    {
    }

    public void WriteText(string text)
    {
    }

    public void ToolStarted(string callId, string name, string arguments)
    {
    }

    public void ToolUpdated(string callId, string name, string arguments)
    {
    }

    public void ToolFinished(string callId, string name, string? error, string result)
    {
    }

    public void WriteLine()
    {
    }
}

internal sealed class TerminalChatOutput : IChatOutput
{
    public void AgentStarted()
    {
    }

    public void AgentFinished(string assistantText, bool cancelled)
    {
    }

    public void AssistantMessageStarted()
    {
    }

    public void AssistantMessageFinished(string assistantText)
    {
    }

    public void WriteText(string text) => Console.Write(text);

    public void ToolStarted(string callId, string name, string arguments) =>
        Console.WriteLine($"\n[tool:start] {name} {arguments}");

    public void ToolUpdated(string callId, string name, string arguments) =>
        Console.WriteLine($"\n[tool:update] {name} {arguments}");

    public void ToolFinished(string callId, string name, string? error, string result) =>
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

    public void AgentStarted()
    {
        _writer.Write(new { type = "agent_start" });
        _writer.Write(new { type = "turn_start" });
    }

    public void AgentFinished(string assistantText, bool cancelled)
    {
        _writer.Write(new
        {
            type = "turn_end",
            message = new
            {
                role = "assistant",
                content = new[] { new { type = "text", text = assistantText } },
            },
            toolResults = Array.Empty<object>(),
        });
        _writer.Write(new { type = "agent_end", cancelled });
    }

    public void AssistantMessageStarted() =>
        _writer.Write(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<object>() } });

    public void AssistantMessageFinished(string assistantText) =>
        _writer.Write(new
        {
            type = "message_end",
            message = new { role = "assistant", content = new[] { new { type = "text", text = assistantText } } },
        });

    public void WriteText(string text) =>
        _writer.Write(new
        {
            type = "message_update",
            message = new { role = "assistant", content = Array.Empty<object>() },
            assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = text },
        });

    public void ToolStarted(string callId, string name, string arguments) =>
        _writer.Write(new { type = "tool_execution_start", toolCallId = callId, toolName = name, args = ParseJson(arguments) });

    public void ToolUpdated(string callId, string name, string arguments) =>
        _writer.Write(new { type = "tool_execution_update", toolCallId = callId, toolName = name, args = ParseJson(arguments) });

    public void ToolFinished(string callId, string name, string? error, string result) =>
        _writer.Write(new { type = "tool_execution_end", toolCallId = callId, toolName = name, result, isError = error is not null });

    public void WriteLine()
    {
    }

    private static object ParseJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return value;
        }
    }
}

internal sealed record RpcCommandEnvelope(
    string? Id,
    string Type,
    string? Message,
    string? StreamingBehavior,
    string? Mode,
    string? Name);

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
            GetString(root, "mode"),
            GetString(root, "name"));
    }

    public static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
