using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>Application-owned conversation. MAF sessions are rebuilt from the selected branch.</summary>
public sealed class ConversationSession
{
    public const int FormatVersion = 2;
    public string Id { get; }
    public string WorkingDirectory { get; }
    public string Model { get; private set; }
    public string? Endpoint { get; private set; }
    public string? Name { get; private set; }
    public ConversationTree Tree { get; }

    public ConversationSession(string workingDirectory, string model, string? endpoint)
        : this(Guid.NewGuid().ToString("N"), Path.GetFullPath(workingDirectory), model, endpoint, null, new ConversationTree()) { }

    private ConversationSession(string id, string cwd, string model, string? endpoint, string? name, ConversationTree tree)
    {
        Id = id;
        WorkingDirectory = cwd;
        Model = model;
        Endpoint = endpoint;
        Name = name;
        Tree = tree;
    }

    public void Rename(string? name) => Name = name;
    public void SelectModel(string model, string? endpoint)
    {
        Model = model;
        Endpoint = endpoint;
        Tree.Append("model_change", JsonSerializer.SerializeToElement(new { model, endpoint }));
    }

    // M.E.AI deliberately does not serialize function exceptions. Capture failures explicitly
    // so a failed invocation cannot become successful after restoring a branch.
    public void Append(ChatMessage message)
    {
        var errors = message.Contents.Select((content, index) => new { content, index })
            .Where(item => item.content is FunctionResultContent { Exception: not null } or FunctionCallContent { Exception: not null })
            .Select(item => new ToolError(item.index, item.content is FunctionResultContent ? "result" : "call",
                item.content is FunctionResultContent result ? result.Exception!.Message : ((FunctionCallContent)item.content).Exception!.Message))
            .ToArray();
        Tree.Append("chat", JsonSerializer.SerializeToElement(new ChatRecord(
            JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions), errors)));
    }

    public List<ChatMessage> ActiveMessages() => Tree.ActivePath()
        .Where(node => node.Type == "chat")
        .Select(node => Restore(node.Payload, node.Id))
        .ToList();

    private static ChatMessage Restore(JsonElement payload, string id)
    {
        var record = payload.Deserialize<ChatRecord>() ?? throw new InvalidDataException($"Missing message at {id}.");
        if (record.Message.ValueKind != JsonValueKind.Object || record.Errors is null)
            throw new InvalidDataException($"Incomplete chat record at {id}.");
        var message = record.Message.Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)
            ?? throw new InvalidDataException($"Missing message at {id}.");
        foreach (var error in record.Errors)
        {
            if (error.Index < 0 || error.Index >= message.Contents.Count) throw new InvalidDataException($"Invalid tool failure at {id}.");
            var exception = new ToolFailureException(error.Message);
            if (error.Type == "result" && message.Contents[error.Index] is FunctionResultContent result) result.Exception = exception;
            else if (error.Type == "call" && message.Contents[error.Index] is FunctionCallContent call) call.Exception = exception;
            else throw new InvalidDataException($"Invalid tool failure kind at {id}.");
        }
        return message;
    }

    public ConversationSession Fork() => new(Guid.NewGuid().ToString("N"), WorkingDirectory, Model, Endpoint, Name, Tree.CloneActivePath());

    public string ToJson()
    {
        var document = new Document(FormatVersion, Id, WorkingDirectory, Model, Endpoint, Name, Tree.HeadId, Tree.Entries.ToArray());
        return JsonSerializer.Serialize(document);
    }

    public static ConversationSession Parse(string json)
    {
        var document = JsonSerializer.Deserialize<Document>(json) ?? throw new InvalidDataException("Empty session.");
        if (document.Version is not (1 or FormatVersion) || string.IsNullOrWhiteSpace(document.Id) ||
            string.IsNullOrWhiteSpace(document.WorkingDirectory) || string.IsNullOrWhiteSpace(document.Model) || document.Entries is null)
            throw new InvalidDataException("Unsupported or incomplete session document.");
        if (document.Entries.Any(entry => entry.Type == "chat" && entry.Payload.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("Session contains an invalid chat entry.");
        if (document.Version == 1 && document.Entries.Any(entry => entry.Type == "chat" &&
            (entry.Payload.Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)?.Contents.Any(content =>
                content is FunctionCallContent or FunctionResultContent) ?? false)))
            throw new InvalidDataException("v1 tool turns did not persist failure state; refusing an unsafe import.");
        var entries = document.Version == 1
            ? document.Entries.Select(entry => entry.Type == "chat"
                ? entry with { Payload = JsonSerializer.SerializeToElement(new ChatRecord(entry.Payload, [])) }
                : entry).ToArray()
            : document.Entries;
        // Validate every branch, not merely the currently selected path.
        foreach (var entry in entries.Where(entry => entry.Type == "chat")) _ = Restore(entry.Payload, entry.Id);
        var tree = new ConversationTree(entries);
        tree.Select(document.HeadId); // An explicit null selection is distinct from the last appended entry.
        return new ConversationSession(document.Id, document.WorkingDirectory, document.Model, document.Endpoint, document.Name, tree);
    }

    private sealed record ChatRecord(JsonElement Message, ToolError[]? Errors);
    private sealed record ToolError(int Index, string Type, string Message);
    private sealed record Document(int Version, string Id, string WorkingDirectory, string Model, string? Endpoint,
        string? Name, string? HeadId, ConversationNode[] Entries);
}
