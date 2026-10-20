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
        int sourceMessageCount, IReadOnlyList<int?> messageLineage,
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
            var mapBoundary = CreateBoundaryMapper(sourceMessageCount, messageLineage);
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

    private static Func<int, int> CreateBoundaryMapper(int sourceMessageCount,
        IReadOnlyList<int?> messageLineage)
    {
        var sourceOccurrences = new Dictionary<int, int>();
        foreach (var sourceIndex in messageLineage.OfType<int>())
            sourceOccurrences[sourceIndex] = sourceOccurrences.GetValueOrDefault(sourceIndex) + 1;
        var ambiguousSources = sourceOccurrences.Where(pair => pair.Value != 1)
            .Select(pair => pair.Key).ToHashSet();
        var sourceCount = Math.Max(0, sourceMessageCount);

        return boundary =>
        {
            var sourceBoundary = Math.Clamp(boundary, 0, sourceCount);
            var lastPrefixPosition = -1;
            for (var position = 0; position < messageLineage.Count; position++)
                if (messageLineage[position] is { } sourceIndex && sourceIndex >= 0 &&
                    sourceIndex < sourceBoundary && sourceIndex < sourceCount && !ambiguousSources.Contains(sourceIndex))
                    lastPrefixPosition = position;

            // New or replaced messages have no lineage. Keep the revision immediately after
            // the last retained source-prefix message, before any unowned messages that follow it.
            return Math.Clamp(lastPrefixPosition + 1, 0, messageLineage.Count);
        };
    }

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
