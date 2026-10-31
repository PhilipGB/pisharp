using System.Text.Json;
using PiSharp.Runtime.VirtualModels;
using static PiSharp.Runtime.Sessions.PiJsonlEntryCodec;

namespace PiSharp.Runtime.Sessions;

/// <summary>Reads and writes the current Pi JSONL session tree while PiSharp keeps its own runtime format.</summary>
public static class PiJsonlSessionInterchange
{
    /// <summary>Imports Pi v1-v3 JSONL, migrating legacy links and retaining original Pi records for export.</summary>
    public static ConversationSession Import(string jsonl, string? workingDirectoryOverride = null,
        string? workingDirectoryFallback = null) =>
        PiJsonlSessionImporter.Import(jsonl, workingDirectoryOverride, workingDirectoryFallback);

    /// <summary>Serializes all branches as current Pi v3 JSONL; unknown imported records are preserved verbatim.</summary>
    public static string Export(ConversationSession session) => PiJsonlSessionWriter.Export(session);

    /// <summary>Writes Pi JSONL without replacing an existing export; files are user-private on Unix.</summary>
    public static Task ExportToFileAsync(ConversationSession session, string path,
        CancellationToken cancellationToken = default) => PiJsonlSessionWriter.ExportToFileAsync(session, path, cancellationToken);

    /// <summary>Projects stored entries to Pi v3 entry objects without adding a session header or branch anchor.</summary>
    public static IReadOnlyList<JsonElement> ProjectEntries(ConversationSession session, string? api = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var nodesById = session.Tree.Entries.ToDictionary(node => node.Id, StringComparer.Ordinal);
        return session.Tree.Entries
            .Where(node => !PiJsonlEntryCodec.IsSuppressedInterrupted(node, nodesById))
            .Select(node =>
                JsonSerializer.SerializeToElement(ProjectEntry(session, node, api))).ToArray();
    }

    /// <summary>Resolve the current branch's Pi thinking-level state, if the imported log records one.</summary>
    public static string? GetThinkingLevel(ConversationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Tree.ActivePath().Where(node => node.Type == "thinking_level_change")
            .Select(node => StringProperty(node.Payload, "thinkingLevel") ??
                (OriginalEntry(node) is { } entry ? StringProperty(entry, "thinkingLevel") : null))
            .LastOrDefault(level => level is not null);
    }

    /// <summary>Restores a registered virtual model selection that physical responses cannot represent.</summary>
    public static bool RestoreRegisteredVirtualModelSelection(ConversationSession session,
        VirtualModelRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(registry);
        var change = session.Tree.ActivePath().LastOrDefault(node => node.Type == "model_change");
        if (change is null) return false;
        var original = OriginalEntry(change);
        var model = original is { } entry ? StringProperty(entry, "modelId") : StringProperty(change.Payload, "model");
        var provider = original is { } source ? StringProperty(source, "provider") : StringProperty(change.Payload, "provider");
        if (model is null || provider is null || registry.Get(provider, model) is null) return false;
        session.RestoreModelMetadata(model, null, provider);
        return true;
    }

}
