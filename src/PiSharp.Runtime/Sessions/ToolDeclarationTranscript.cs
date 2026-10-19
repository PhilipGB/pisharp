using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

internal sealed class ToolDeclarationTranscript
{
    private readonly List<(int AfterMessageCount, ChatMessage Message)> _revisions = [];
    private JsonElement? _sourceHead;

    internal void Restore(JsonElement? saved)
    {
        _revisions.Clear();
        _sourceHead = null;
        if (saved is not { ValueKind: JsonValueKind.Array } revisions) return;
        foreach (var revision in revisions.EnumerateArray())
            _revisions.Add((revision.GetProperty("afterMessageCount").GetInt32(),
                revision.GetProperty("message").Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)!));
    }

    internal JsonElement Serialize() => JsonSerializer.SerializeToElement(_revisions.Select(revision => new
    {
        afterMessageCount = revision.AfterMessageCount,
        message = JsonSerializer.SerializeToElement(revision.Message, AIJsonUtilities.DefaultOptions)
    }));

    internal static JsonElement? ApplyContextMutation(JsonElement transcript,
        IReadOnlyList<ChatMessage> sourceMessages, IReadOnlyList<ChatMessage> updatedMessages,
        string? instructions, bool instructionsChanged, bool messageStructureChanged, bool removeSystemContext)
    {
        var revisions = transcript.ValueKind == JsonValueKind.Array
            ? transcript.EnumerateArray().Select(revision =>
            {
                var message = revision.GetProperty("message").Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)!;
                return (AfterMessageCount: revision.GetProperty("afterMessageCount").GetInt32(), Message: message);
            }).ToList()
            : [];

        if (messageStructureChanged)
        {
            var mapBoundary = CreateBoundaryMapper(sourceMessages, updatedMessages);
            for (var index = 0; index < revisions.Count; index++)
                revisions[index] = (mapBoundary(revisions[index].AfterMessageCount), revisions[index].Message);
        }

        if (removeSystemContext)
        {
            foreach (var revision in revisions) SystemMessageTranscript.ClearText(revision.Message);
            revisions.RemoveAll(revision => !SystemMessageTranscript.HasToolRevision(revision.Message));
        }
        else if (instructionsChanged)
        {
            var initialIndex = revisions.FindIndex(revision => revision.AfterMessageCount == 0);
            if (initialIndex < 0 && instructions is not null)
            {
                revisions.Insert(0, (0, new ChatMessage(ChatRole.System, instructions)));
            }
            else if (initialIndex >= 0)
            {
                var initial = revisions[initialIndex].Message;
                SystemMessageTranscript.ReplaceText(initial, instructions, clearSections: true);
            }
        }

        if (revisions.Count == 0) return null;
        return JsonSerializer.SerializeToElement(revisions.Select(revision => new
        {
            afterMessageCount = revision.AfterMessageCount,
            message = JsonSerializer.SerializeToElement(revision.Message, AIJsonUtilities.DefaultOptions)
        }));
    }

    private static Func<int, int> CreateBoundaryMapper(IReadOnlyList<ChatMessage> sourceMessages,
        IReadOnlyList<ChatMessage> updatedMessages)
    {
        try
        {
            var source = sourceMessages.Select(MessageSignature).ToArray();
            var updated = updatedMessages.Select(MessageSignature).ToArray();
            var positions = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
            for (var index = 0; index < updated.Length; index++)
            {
                if (!positions.TryGetValue(updated[index], out var queue)) positions[updated[index]] = queue = new();
                queue.Enqueue(index);
            }

            var sourceToUpdated = new int?[source.Length];
            var lastUpdated = -1;
            foreach (var (signature, sourceIndex) in source.Select((signature, index) => (signature, index)))
            {
                if (!positions.TryGetValue(signature, out var queue)) continue;
                while (queue.Count > 0 && queue.Peek() <= lastUpdated) queue.Dequeue();
                if (queue.Count == 0) continue;
                lastUpdated = queue.Dequeue();
                sourceToUpdated[sourceIndex] = lastUpdated;
            }

            return boundary =>
            {
                if (boundary <= 0) return 0;
                if (boundary >= sourceToUpdated.Length) return updated.Length;
                for (var index = boundary; index < sourceToUpdated.Length; index++)
                    if (sourceToUpdated[index] is { } mapped) return mapped;
                return updated.Length;
            };
        }
        catch (Exception)
        {
            return boundary => Math.Clamp(boundary, 0, updatedMessages.Count);
        }
    }

    private static string MessageSignature(ChatMessage message) =>
        JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions);

    internal JsonElement Project(IReadOnlyList<ChatMessage> messages, ChatOptions options)
    {
        var head = messages.FirstOrDefault() is { } message
            ? JsonSerializer.SerializeToElement(ChatMessageProperties.WithoutRequestAttribution(message), AIJsonUtilities.DefaultOptions)
            : (JsonElement?)null;
        if (_sourceHead is { } previousHead && (head is null || !JsonElement.DeepEquals(previousHead, head.Value)))
            _revisions.Clear();
        _sourceHead = head;
        var current = (options.Tools ?? []).OfType<AIFunctionDeclaration>().Select(ToolDeclarationJson.Create).ToArray();
        var projected = Replay(messages);
        if (projected.FirstOrDefault()?.Role != ChatRole.System)
        {
            var initial = new ChatMessage(ChatRole.System, options.Instructions ?? "");
            SystemMessageTranscript.ImportMetadata(initial, JsonSerializer.SerializeToElement(new { toolsAdded = current }));
            _revisions.Add((0, initial));
            projected = Replay(messages);
        }
        else
        {
            var previous = SystemMessageTranscript.CurrentTools(projected).ToDictionary(tool => tool.GetProperty("name").GetString()!, StringComparer.Ordinal);
            var next = current.ToDictionary(tool => tool.GetProperty("name").GetString()!, StringComparer.Ordinal);
            var added = current.Where(tool => !previous.TryGetValue(tool.GetProperty("name").GetString()!, out var old) ||
                JsonSerializer.Serialize(old) != JsonSerializer.Serialize(tool)).ToArray();
            var removed = previous.Where(tool => !next.TryGetValue(tool.Key, out var value) || JsonSerializer.Serialize(tool.Value) != JsonSerializer.Serialize(value))
                .Select(tool => new { name = tool.Key }).ToArray();
            if (added.Length > 0 || removed.Length > 0)
            {
                var update = new ChatMessage(ChatRole.System, "");
                SystemMessageTranscript.ImportMetadata(update, JsonSerializer.SerializeToElement(new { toolsAdded = added, toolsRemoved = removed }));
                _revisions.Add((messages.Count, update));
                projected = Replay(messages);
            }
        }
        return Serialize();
    }

    private IReadOnlyList<ChatMessage> Replay(IReadOnlyList<ChatMessage> messages)
    {
        var projected = new List<ChatMessage>();
        for (var index = 0; index <= messages.Count; index++)
        {
            projected.AddRange(_revisions.Where(revision => Math.Min(revision.AfterMessageCount, messages.Count) == index)
                .Select(revision => revision.Message));
            if (index < messages.Count) projected.Add(messages[index]);
        }
        return projected;
    }
}
