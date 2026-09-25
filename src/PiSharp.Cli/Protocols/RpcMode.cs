using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Cli.Protocols;

/// <summary>Experimental subset of Pi RPC. Unsupported commands return errors, never false success.</summary>
public sealed class RpcMode(TextReader input, TextWriter output, ConversationRun run,
    Func<CancellationToken, Task>? save = null, PiSharp.Runtime.Resources.ResourceCatalog? resources = null,
    Func<CancellationToken, Task<IReadOnlyList<ModelDescriptor>>>? discoverModels = null,
    ExtensionRegistration? extensions = null, Func<string?>? promptPreflight = null)
{
    private readonly JsonLineWriter _writer = new(output);
    private readonly ConcurrentDictionary<Guid, BashOperation> _bashOperations = new();
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
                        case "get_available_models":
                            if (busy) { await RespondAsync(id, type, false, "Wait until the active prompt settles."); break; }
                            if (discoverModels is null) { await RespondAsync(id, type, false, "Model discovery is unavailable."); break; }
                            try
                            {
                                var models = await discoverModels(cancellationToken);
                                await _writer.EmitAsync(new
                                {
                                    id,
                                    type = "response",
                                    command = type,
                                    success = true,
                                    data = new { models }
                                }, cancellationToken);
                            }
                            catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException or TaskCanceledException)
                            { await RespondAsync(id, type, false, error.Message); }
                            break;
                        case "compact":
                            if (busy) { await RespondAsync(id, type, false, "Wait until the active prompt settles."); break; }
                            if (root.TryGetProperty("instructions", out var focus) && focus.ValueKind != JsonValueKind.String)
                            { await RespondAsync(id, type, false, "Instructions must be text."); break; }
                            try
                            {
                                var compacted = await run.CompactAsync(root.TryGetProperty("instructions", out focus) ? focus.GetString() : null, cancellationToken);
                                await _writer.EmitAsync(new { id, type = "response", command = type, success = true, data = new { compacted } }, cancellationToken);
                            }
                            catch (Exception error) when (error is not OperationCanceledException)
                            { await RespondAsync(id, type, false, error.Message); }
                            break;
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
                            var queue = run.GetPendingPrompts();
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
                                    steering = queue.Steering,
                                    followUp = queue.FollowUp,
                                    format = "pisharp",
                                    version = ConversationSession.FormatVersion
                                }
                            }, cancellationToken);
                            break;
                        case "export_html":
                            if (busy) { await RespondAsync(id, type, false, "Wait until the active prompt settles."); break; }
                            if (root.TryGetProperty("outputPath", out var outputPath) &&
                                (outputPath.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(outputPath.GetString())))
                            { await RespondAsync(id, type, false, "outputPath must be a nonempty string."); break; }
                            try
                            {
                                var path = Path.GetFullPath(root.TryGetProperty("outputPath", out outputPath)
                                    ? outputPath.GetString()! : Path.Combine(run.Conversation.WorkingDirectory,
                                        $"pisharp-{run.Conversation.Id[..12]}.html"));
                                await SessionExport.ExportHtmlAsync(run.Conversation, path, cancellationToken);
                                await _writer.EmitAsync(new
                                {
                                    id,
                                    type = "response",
                                    command = type,
                                    success = true,
                                    data = new { path }
                                }, cancellationToken);
                            }
                            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
                            { await RespondAsync(id, type, false, error.Message); }
                            break;
                        case "get_session_stats":
                            if (busy) { await RespondAsync(id, type, false, "Wait until the active prompt settles."); break; }
                            await _writer.EmitAsync(new
                            {
                                id,
                                type = "response",
                                command = type,
                                success = true,
                                data = SessionStatistics.Calculate(run.Conversation)
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
                            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(message.GetString()) || root.TryGetProperty("images", out _))
                            { await RespondAsync(id, type, false, "A nonempty text message is required; images are not supported."); break; }
                            string? promptStreamingBehavior = null;
                            if (root.TryGetProperty("streamingBehavior", out var requestedBehavior))
                            {
                                if (requestedBehavior.ValueKind != JsonValueKind.String ||
                                    requestedBehavior.GetString() is not ("steer" or "followUp"))
                                { await RespondAsync(id, type, false, "streamingBehavior must be 'steer' or 'followUp'."); break; }
                                promptStreamingBehavior = requestedBehavior.GetString();
                            }
                            string expanded;
                            try
                            {
                                expanded = resources is null ? message.GetString()! :
                                await resources.ResolveInputAsync(message.GetString()!, cancellationToken);
                            }
                            catch (Exception error) when (error is ArgumentException or IOException)
                            { await RespondAsync(id, type, false, error.Message); break; }
                            if (busy)
                            {
                                if (promptStreamingBehavior is null)
                                {
                                    await RespondAsync(id, type, false,
                                        "The active run requires streamingBehavior 'steer' or 'followUp'.");
                                    break;
                                }
                                var queued = promptStreamingBehavior == "steer"
                                    ? run.TrySteer(expanded) : run.TryFollowUp(expanded);
                                await RespondPromptAsync(id, queued, queued ? "queued" : null,
                                    queued ? null : "The active run is already settling; submit the prompt again.");
                                break;
                            }
                            string? preflightError;
                            try { preflightError = promptPreflight?.Invoke(); }
                            catch (Exception error)
                            { await RespondAsync(id, type, false, error.Message); break; }
                            if (!string.IsNullOrWhiteSpace(preflightError))
                            { await RespondAsync(id, type, false, preflightError); break; }
                            _abort?.Dispose();
                            _abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            _active = ExecuteAsync(id, expanded, _abort.Token);
                            break;
                        case "bash":
                            if (!root.TryGetProperty("command", out var bashCommand) || bashCommand.ValueKind != JsonValueKind.String)
                            { await RespondAsync(id, type, false, "A string command is required."); break; }
                            if (root.TryGetProperty("excludeFromContext", out var excludeFromContext) &&
                                excludeFromContext.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            { await RespondAsync(id, type, false, "excludeFromContext must be a boolean."); break; }
                            StartBash(id, bashCommand.GetString()!, excludeFromContext.ValueKind == JsonValueKind.True, cancellationToken);
                            break;
                        case "abort_bash":
                            run.AbortBash();
                            foreach (var operation in _bashOperations.Values)
                            {
                                try { operation.Cancellation.Cancel(); }
                                catch (ObjectDisposedException) { }
                            }
                            await RespondAsync(id, type, true);
                            break;
                        case "steer":
                        case "follow_up":
                            if (!busy) { await RespondAsync(id, type, false, "There is no active run to queue input for."); break; }
                            if (!TryGetTextMessage(root, out var queuedMessage, out var queueError))
                            { await RespondAsync(id, type, false, queueError); break; }
                            string queuedExpanded;
                            try
                            {
                                queuedExpanded = resources is null ? queuedMessage! :
                                    await resources.ResolveInputAsync(queuedMessage!, cancellationToken);
                            }
                            catch (Exception error) when (error is ArgumentException or IOException)
                            { await RespondAsync(id, type, false, error.Message); break; }
                            var added = type == "steer" ? run.TrySteer(queuedExpanded) : run.TryFollowUp(queuedExpanded);
                            await RespondQueuedInputAsync(id, type, added,
                                added ? null : "The active run is already settling.");
                            break;
                        case "clear_queue":
                            var pending = run.ClearPendingPrompts();
                            await _writer.EmitAsync(new
                            {
                                id,
                                type = "response",
                                command = type,
                                success = true,
                                data = new { steering = pending.Steering, followUp = pending.FollowUp }
                            }, cancellationToken);
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
            foreach (var operation in _bashOperations.Values)
            {
                try { operation.Cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            var bashOperations = _bashOperations.Values.ToArray();
            if (bashOperations.Length > 0)
                await Task.WhenAll(bashOperations.Select(operation => operation.Completed.Task));
            if (_active is { IsCompleted: false })
            {
                _abort?.Cancel();
                try { await _active; } catch (OperationCanceledException) { }
            }
            _abort?.Dispose();
        }
    }

    private static bool TryGetTextMessage(JsonElement root, out string? message, out string? error)
    {
        message = null;
        error = null;
        if (!root.TryGetProperty("message", out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(message = value.GetString()) || root.TryGetProperty("images", out _))
        {
            error = "A nonempty text message is required; images are not supported.";
            return false;
        }
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

    private sealed class TreeNode(PiSharp.Core.ConversationNode entry)
    {
        public PiSharp.Core.ConversationNode Entry { get; } = entry;
        public List<TreeNode> Children { get; } = [];
    }

    private async Task ExecuteAsync(JsonElement? id, string message, CancellationToken token)
    {
        var responded = false;
        try
        {
            await new JsonEventMode(_writer).RunAsync(run, message, token, async item =>
            {
                if (responded) return;
                if (item.Type == "prompt_accepted")
                {
                    responded = true;
                    await RespondPromptAsync(id, true, "started");
                }
                else if (item.Type == "prompt_rejected")
                {
                    responded = true;
                    await RespondPromptAsync(id, false, error: item.Error ?? "Prompt was rejected.");
                }
            });
            if (save is not null) await save(CancellationToken.None);
        }
        catch (Exception error)
        {
            if (!responded)
            {
                responded = true;
                await RespondPromptAsync(id, false, error: error.Message);
            }
            else await _writer.EmitAsync(new { type = "error", error = error.Message }, CancellationToken.None);
        }
    }

    private void StartBash(JsonElement? id, string command, bool excludeFromContext, CancellationToken cancellationToken)
    {
        var key = Guid.NewGuid();
        var operation = new BashOperation(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        if (!_bashOperations.TryAdd(key, operation)) throw new InvalidOperationException("Could not register the bash operation.");
        _ = ExecuteBashAsync(key, operation, id, command, excludeFromContext);
    }

    private async Task ExecuteBashAsync(Guid key, BashOperation operation, JsonElement? id, string command,
        bool excludeFromContext)
    {
        try
        {
            var correlationId = GetCorrelationId(id);
            var result = await ExecuteUserBashAsync(correlationId, command, excludeFromContext, operation.Cancellation.Token);
            var recordedNow = run.RecordBashResult(command, result, excludeFromContext);
            if (recordedNow && save is not null) await save(CancellationToken.None);
            var data = new Dictionary<string, object?>
            {
                ["output"] = result.Output,
                ["cancelled"] = result.Cancelled,
                ["truncated"] = result.Truncated
            };
            if (result.ExitCode is { } exitCode) data["exitCode"] = exitCode;
            if (result.FullOutputPath is not null) data["fullOutputPath"] = result.FullOutputPath;
            await _writer.EmitAsync(new { id, type = "response", command = "bash", success = true, data });
        }
        catch (Exception error)
        {
            await RespondAsync(id, "bash", false, error.Message);
        }
        finally
        {
            operation.Completed.TrySetResult();
            _bashOperations.TryRemove(key, out _);
            operation.Cancellation.Dispose();
        }
    }

    private async Task<BashExecutionResult> ExecuteUserBashAsync(string? correlationId, string command,
        bool excludeFromContext, CancellationToken cancellationToken)
    {
        if (extensions is not null)
        {
            var context = new UserBashContext(command, excludeFromContext, run.Conversation.WorkingDirectory,
                delta => EmitBashUpdateAsync(correlationId, delta));
            foreach (var handler in extensions.UserBashHandlers)
            {
                try
                {
                    if (await handler(context, cancellationToken) is { } result) return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    await _writer.EmitAsync(new
                    {
                        type = "event",
                        format = "pisharp",
                        data = new AgentLifecycleEvent("extension_error", OperationId: correlationId, Error: error.Message)
                    }, CancellationToken.None);
                }
            }
        }

        return await run.ExecuteBashAsync(command,
            delta => EmitBashUpdateAsync(correlationId, delta).GetAwaiter().GetResult(), cancellationToken);
    }

    private Task EmitBashUpdateAsync(string? id, string delta) => _writer.EmitAsync(new
    {
        type = "event",
        format = "pisharp",
        data = new AgentLifecycleEvent("bash_execution_update", Text: delta, Tool: "bash", OperationId: id)
    });

    private static string? GetCorrelationId(JsonElement? id)
    {
        if (id is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private sealed class BashOperation(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private Task RespondAsync(JsonElement? id, string command, bool success, string? error = null) =>
        _writer.EmitAsync(new { id, type = "response", command, success, error });

    private Task RespondQueuedInputAsync(JsonElement? id, string command, bool success, string? error = null) =>
        success
            ? _writer.EmitAsync(new { id, type = "response", command, success = true, data = new { disposition = "queued" } })
            : RespondAsync(id, command, false, error);

    private Task RespondPromptAsync(JsonElement? id, bool success, string? disposition = null, string? error = null) =>
        success
            ? _writer.EmitAsync(new { id, type = "response", command = "prompt", success = true, data = new { disposition } })
            : RespondAsync(id, "prompt", false, error);
}
