using System.Text.Json;

namespace PiSharp.Core;

public sealed record SessionHeader(
    string Type,
    int Version,
    string SessionId,
    string WorkingDirectory,
    string Model,
    DateTimeOffset CreatedAtUtc)
{
    public static SessionHeader Create(string workingDirectory, string model) =>
        new(
            "session",
            1,
            Guid.NewGuid().ToString("N"),
            Path.GetFullPath(workingDirectory),
            model,
            DateTimeOffset.UtcNow);
}

public sealed record SessionTurn(
    string Type,
    string Id,
    string? ParentId,
    DateTimeOffset CreatedAtUtc,
    string UserMessage,
    string AssistantMessage,
    JsonElement AgentState)
{
    public static SessionTurn Create(
        string? parentId,
        string userMessage,
        string assistantMessage,
        JsonElement agentState) =>
        new(
            "turn",
            Guid.NewGuid().ToString("N"),
            parentId,
            DateTimeOffset.UtcNow,
            userMessage,
            assistantMessage,
            agentState.Clone());
}
