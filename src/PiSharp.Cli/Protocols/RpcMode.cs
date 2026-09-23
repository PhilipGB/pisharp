using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

/// <summary>Experimental subset of Pi RPC. Unsupported commands return errors, never false success.</summary>
public sealed class RpcMode(TextReader input, TextWriter output, ConversationRun run,
    Func<CancellationToken, Task>? save = null, PiSharp.Runtime.Resources.ResourceCatalog? resources = null)
{
    private readonly JsonLineWriter _writer = new(output);
    private CancellationTokenSource? _abort;
    private Task? _active;

    public async Task ServeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (await input.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.Length > 1024 * 1024)
                {
                    await RespondAsync(null, "unknown", false, "Command exceeds 1MB.");
                    continue;
                }
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException error) { await RespondAsync(null, "unknown", false, error.Message); continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String)
                    {
                        await RespondAsync(null, "unknown", false, "Command must be an object with a type.");
                        continue;
                    }
                    var type = kind.GetString()!;
                    var id = root.TryGetProperty("id", out var requestId) ? requestId.Clone() : (JsonElement?)null;
                    var busy = _active is { IsCompleted: false };
                    switch (type)
                    {
                        case "get_commands":
                            await _writer.EmitAsync(new
                            {
                                id,
                                type = "response",
                                command = type,
                                success = true,
                                data = new
                                {
                                    commands = (resources?.Prompts.Select(item => new { name = item.Name, description = item.Description, source = "prompt" })
                                    ?? []).Concat(resources?.Skills.Select(item => new { name = "skill:" + item.Name, description = item.Description, source = "skill" }) ?? []).ToArray()
                                }
                            }, cancellationToken);
                            break;
                        case "get_state":
                            await _writer.EmitAsync(new
                            {
                                id,
                                type = "response",
                                command = type,
                                success = true,
                                data = new
                                {
                                    model = run.Conversation.Model,
                                    isStreaming = busy,
                                    sessionId = run.Conversation.Id,
                                    sessionName = run.Conversation.Name,
                                    messageCount = run.Conversation.ActiveMessages().Count,
                                    format = "pisharp",
                                    version = ConversationSession.FormatVersion
                                }
                            }, cancellationToken);
                            break;
                        case "get_messages":
                            if (busy) { await RespondAsync(id, type, false, "Wait until the active prompt settles."); break; }
                            await _writer.EmitAsync(new
                            {
                                id,
                                type = "response",
                                command = type,
                                success = true,
                                data = new
                                {
                                    messages = run.Conversation.ActiveMessages().Select(message =>
                                    JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions)).ToArray()
                                }
                            }, cancellationToken);
                            break;
                        case "get_last_assistant_text":
                            if (busy) { await RespondAsync(id, type, false, "Wait until the active prompt settles."); break; }
                            await _writer.EmitAsync(new
                            {
                                id,
                                type = "response",
                                command = type,
                                success = true,
                                data = new
                                {
                                    text = run.Conversation.ActiveMessages().LastOrDefault(message =>
                                    message.Role == ChatRole.Assistant)?.Text
                                }
                            }, cancellationToken);
                            break;
                        case "get_entries":
                            if (busy) { await RespondAsync(id, type, false, "Wait until the active prompt settles."); break; }
                            var entries = run.Conversation.Tree.Entries;
                            var index = -1;
                            if (root.TryGetProperty("since", out var since))
                            {
                                if (since.ValueKind != JsonValueKind.String ||
                                    (index = entries.ToList().FindIndex(entry => entry.Id == since.GetString())) < 0)
                                { await RespondAsync(id, type, false, "Unknown entry cursor."); break; }
                            }
                            await _writer.EmitAsync(new
                            {
                                id,
                                type = "response",
                                command = type,
                                success = true,
                                data = new
                                {
                                    format = "pisharp",
                                    entries = entries.Skip(index + 1).ToArray(),
                                    leafId = run.Conversation.Tree.HeadId
                                }
                            }, cancellationToken);
                            break;
                        case "get_tree":
                            if (busy) { await RespondAsync(id, type, false, "Wait until the active prompt settles."); break; }
                            await _writer.EmitAsync(new
                            {
                                id,
                                type = "response",
                                command = type,
                                success = true,
                                data = new { format = "pisharp", tree = BuildTree(run.Conversation), leafId = run.Conversation.Tree.HeadId }
                            }, cancellationToken);
                            break;
                        case "set_session_name":
                            if (busy) { await RespondAsync(id, type, false, "Wait until the active prompt settles."); break; }
                            if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                            { await RespondAsync(id, type, false, "A string name is required."); break; }
                            var oldName = run.Conversation.Name;
                            run.Conversation.Rename(name.GetString());
                            try { if (save is not null) await save(cancellationToken); }
                            catch (Exception error)
                            { run.Conversation.Rename(oldName); await RespondAsync(id, type, false, error.Message); break; }
                            await RespondAsync(id, type, true);
                            break;
                        case "prompt":
                            if (busy) { await RespondAsync(id, type, false, "Prompt already streaming; steering and follow-up are not implemented."); break; }
                            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(message.GetString()) || root.TryGetProperty("images", out _))
                            { await RespondAsync(id, type, false, "A nonempty text message is required; images are not supported."); break; }
                            string expanded;
                            try
                            {
                                expanded = resources is null ? message.GetString()! :
                                await resources.ResolveInputAsync(message.GetString()!, cancellationToken);
                            }
                            catch (Exception error) when (error is ArgumentException or IOException)
                            { await RespondAsync(id, type, false, error.Message); break; }
                            _abort?.Dispose();
                            _abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            await RespondAsync(id, type, true);
                            _active = ExecuteAsync(expanded, _abort.Token);
                            break;
                        case "abort":
                            if (busy) { _abort?.Cancel(); try { await _active!; } catch (OperationCanceledException) { } }
                            await RespondAsync(id, type, true);
                            break;
                        default:
                            await RespondAsync(id, type, false, $"Unsupported RPC command: {type}");
                            break;
                    }
                }
            }
        }
        finally
        {
            if (_active is { IsCompleted: false })
            {
                _abort?.Cancel();
                try { await _active; } catch (OperationCanceledException) { }
            }
            _abort?.Dispose();
        }
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

    private sealed class TreeNode(PiSharp.Core.ConversationNode entry)
    {
        public PiSharp.Core.ConversationNode Entry { get; } = entry;
        public List<TreeNode> Children { get; } = [];
    }

    private async Task ExecuteAsync(string message, CancellationToken token)
    {
        try
        {
            await new JsonEventMode(_writer).RunAsync(run, message, token);
            if (save is not null) await save(CancellationToken.None);
        }
        catch (Exception error)
        {
            await _writer.EmitAsync(new { type = "error", error = error.Message }, CancellationToken.None);
        }
    }

    private Task RespondAsync(JsonElement? id, string command, bool success, string? error = null) =>
        _writer.EmitAsync(new { id, type = "response", command, success, error });
}
