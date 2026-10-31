using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Sessions;

/// <summary>Builds detached boundary previews and commits them through the canonical session owner.</summary>
internal sealed class ExtensionBoundaryCoordinator(ConversationSession conversation,
    Func<CancellationToken, Task>? save,
    Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<AgentSession>> restore,
    Action<AgentSession, int> adoptExecution,
    Func<(IReadOnlyList<ChatMessage> Queued, IReadOnlyList<ChatMessage> Custom)> pending)
{
    internal PiSharpBoundaryContextPreview Preview(IReadOnlyList<PiSharpSessionBoundaryDraft> drafts,
        bool beforeSettle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preview = conversation.Snapshot();
        BoundaryDraftProjector.Apply(preview, drafts, cancellationToken);
        var messages = preview.ContextMessages();
        var (queued, custom) = pending();
        var finalRole = messages.LastOrDefault()?.Role;
        var canContinue = messages.Any(message => message.Role != ChatRole.System) && finalRole != ChatRole.Assistant ||
            custom.Count > 0 || (beforeSettle ? finalRole == ChatRole.Assistant && queued.Count > 0 : queued.Count > 0);
        var nodesById = preview.Tree.Entries.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var entries = ConversationContextProjection.ContextEntries(preview.Tree.ActivePath())
            .Where(node => !PiJsonlEntryCodec.IsSuppressedInterrupted(node, nodesById))
            .Select(entry => new PiSharpBoundaryContextEntry(entry.Id, entry.Type,
            System.Text.Json.JsonSerializer.SerializeToElement(PiJsonlEntryCodec.ProjectEntry(preview, entry))));
        return new PiSharpBoundaryContextPreview(entries, messages, queued.Concat(custom), canContinue);
    }

    internal async Task CommitAsync(IReadOnlyList<PiSharpSessionBoundaryDraft> drafts,
        CancellationToken cancellationToken)
    {
        if (drafts.Count == 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        var previousHead = conversation.Tree.HeadId;
        var entries = BoundaryDraftProjector.Apply(conversation, drafts, cancellationToken);
        try
        {
            var messages = conversation.ContextMessages();
            // Prepare a usable execution before publishing the durable change. Neither the
            // running session nor its provider history is altered if restoration or saving fails.
            var execution = await restore(messages, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (save is not null) await save(cancellationToken).ConfigureAwait(false);
            adoptExecution(execution, messages.Count);
        }
        catch
        {
            BoundaryDraftProjector.Rollback(conversation, entries, previousHead);
            throw;
        }
    }
}
