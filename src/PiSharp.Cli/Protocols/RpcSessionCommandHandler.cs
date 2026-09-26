using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcSessionCommandHandler(
    JsonLineWriter output,
    Func<JsonElement?, string, bool, string?, Task> respond,
    Func<ConversationRun> currentRun,
    Func<string?> getThinkingLevel,
    Func<bool> isBusy,
    Func<CancellationToken, Task>? save,
    Func<string?, CancellationToken, Task<bool>>? newSession)
{
    public async Task<bool> TryHandleAsync(string command, JsonElement root, JsonElement? id,
        CancellationToken cancellationToken)
    {
        var run = currentRun();
        var conversation = run.Conversation;
        switch (command)
        {
            case "new_session":
                if (root.TryGetProperty("parentSession", out var parentSession) &&
                    parentSession.ValueKind != JsonValueKind.String)
                {
                    await respond(id, command, false, "parentSession must be a string.");
                    return true;
                }
                if (newSession is null)
                {
                    await respond(id, command, false, "Session replacement is not configured.");
                    return true;
                }
                try
                {
                    var cancelled = await newSession(parentSession.ValueKind == JsonValueKind.String
                        ? parentSession.GetString() : null, cancellationToken);
                    await output.EmitAsync(new
                    {
                        id,
                        type = "response",
                        command,
                        success = true,
                        data = new { cancelled }
                    }, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    await respond(id, command, false, error.Message);
                }
                return true;

            case "get_state":
                var queue = run.GetPendingPrompts();
                await output.EmitAsync(new
                {
                    id,
                    type = "response",
                    command,
                    success = true,
                    data = new
                    {
                        model = conversation.Model,
                        thinkingLevel = getThinkingLevel() ?? "off",
                        isStreaming = isBusy(),
                        sessionId = conversation.Id,
                        sessionName = conversation.Name,
                        messageCount = conversation.ActiveMessages().Count,
                        steering = queue.Steering,
                        followUp = queue.FollowUp,
                        format = "pisharp",
                        version = ConversationSession.FormatVersion
                    }
                }, cancellationToken);
                return true;

            case "get_messages":
                if (isBusy()) return await RejectBusyAsync(id, command);
                await output.EmitAsync(new
                {
                    id,
                    type = "response",
                    command,
                    success = true,
                    data = new
                    {
                        messages = conversation.ActiveMessages().Select(message =>
                            JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions)).ToArray()
                    }
                }, cancellationToken);
                return true;

            case "get_fork_messages":
                if (isBusy()) return await RejectBusyAsync(id, command);
                await output.EmitAsync(new
                {
                    id,
                    type = "response",
                    command,
                    success = true,
                    data = new
                    {
                        messages = conversation.UserMessagesForForking()
                            .Select(message => new { entryId = message.Id, text = message.Text }).ToArray()
                    }
                }, cancellationToken);
                return true;

            case "get_last_assistant_text":
                if (isBusy()) return await RejectBusyAsync(id, command);
                await output.EmitAsync(new
                {
                    id,
                    type = "response",
                    command,
                    success = true,
                    data = new
                    {
                        text = conversation.ActiveMessages().LastOrDefault(message =>
                            message.Role == ChatRole.Assistant)?.Text
                    }
                }, cancellationToken);
                return true;

            case "get_entries":
                if (isBusy()) return await RejectBusyAsync(id, command);
                var entries = conversation.Tree.Entries;
                var index = -1;
                if (root.TryGetProperty("since", out var since))
                {
                    if (since.ValueKind != JsonValueKind.String ||
                        (index = entries.ToList().FindIndex(entry => entry.Id == since.GetString())) < 0)
                    {
                        var cursor = since.ValueKind == JsonValueKind.String ? since.GetString() : since.GetRawText();
                        await respond(id, command, false, $"Entry not found: {cursor}");
                        return true;
                    }
                }
                await output.EmitAsync(new
                {
                    id,
                    type = "response",
                    command,
                    success = true,
                    data = new { format = "pisharp", entries = entries.Skip(index + 1).ToArray(), leafId = conversation.Tree.HeadId }
                }, cancellationToken);
                return true;

            case "get_tree":
                if (isBusy()) return await RejectBusyAsync(id, command);
                await output.EmitAsync(new
                {
                    id,
                    type = "response",
                    command,
                    success = true,
                    data = new { format = "pisharp", tree = BuildTree(conversation), leafId = conversation.Tree.HeadId }
                }, cancellationToken);
                return true;

            case "set_session_name":
                if (isBusy()) return await RejectBusyAsync(id, command);
                if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                {
                    await respond(id, command, false, "A string name is required.");
                    return true;
                }
                var updatedName = name.GetString()!.Trim();
                if (updatedName.Length == 0)
                {
                    await respond(id, command, false, "Session name cannot be empty");
                    return true;
                }
                var previousName = conversation.Name;
                conversation.Rename(updatedName);
                try { if (save is not null) await save(cancellationToken); }
                catch (Exception error)
                {
                    conversation.Rename(previousName);
                    await respond(id, command, false, error.Message);
                    return true;
                }
                await respond(id, command, true, null);
                return true;

            default:
                return false;
        }
    }

    private async Task<bool> RejectBusyAsync(JsonElement? id, string command)
    {
        await respond(id, command, false, "Wait until the active prompt settles.");
        return true;
    }

    private static IReadOnlyList<TreeNode> BuildTree(ConversationSession conversation)
    {
        var lookup = conversation.Tree.Entries.ToDictionary(entry => entry.Id, entry => new TreeNode(entry));
        var roots = new List<TreeNode>();
        foreach (var entry in conversation.Tree.Entries)
        {
            var node = lookup[entry.Id];
            if (entry.ParentId is not null && lookup.TryGetValue(entry.ParentId, out var parent))
                parent.Children.Add(node);
            else roots.Add(node);
        }
        return roots;
    }

    private sealed class TreeNode(ConversationNode entry)
    {
        public ConversationNode Entry { get; } = entry;
        public List<TreeNode> Children { get; } = [];
    }
}
