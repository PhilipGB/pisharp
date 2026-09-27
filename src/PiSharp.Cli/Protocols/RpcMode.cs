using System.Collections.Concurrent;
using System.Text.Json;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Cli.Protocols;

/// <summary>Experimental subset of Pi RPC. Unsupported commands return errors, never false success.</summary>
public sealed class RpcMode(TextReader input, TextWriter output, ConversationRun run,
    Func<CancellationToken, Task>? save = null, PiSharp.Runtime.Resources.ResourceCatalog? resources = null,
    Func<string?, CancellationToken, Task<IReadOnlyList<ModelDescriptor>>>? discoverModels = null,
    ExtensionRegistration? extensions = null, Func<string?>? promptPreflight = null,
    Func<ModelDescriptor, CancellationToken, Task<ModelDescriptor>>? setModel = null,
    Func<ConversationRun>? getCurrentRun = null, Func<string?>? getThinkingLevel = null,
    Func<bool>? isModelScoped = null,
    Func<string, CancellationToken, Task<string>>? setThinkingLevel = null,
    Func<IReadOnlyList<string>>? getAvailableThinkingLevels = null, Func<bool>? supportsThinking = null,
    Func<string?>? getApi = null,
    Func<string, CancellationToken, Task<string>>? setThinkingLevelDuringRun = null,
    Func<bool, CancellationToken, Task>? persistRetryEnabled = null,
    Func<string?, CancellationToken, Task<bool>>? newSession = null,
    Func<string, ConversationSession, string, CancellationToken, Task<string?>>? forkSession = null,
    Func<ConversationSession, string?, CancellationToken, Task<bool>>? cloneSession = null,
    Func<string, CancellationToken, Task<bool>>? switchSession = null,
    Func<PiSharp.Runtime.Resources.ResourceCatalog?>? getCurrentResources = null,
    Func<ExtensionRegistration?>? getCurrentExtensions = null,
    Func<bool, PromptDeliveryMode, CancellationToken, Task>? persistQueueMode = null,
    Func<JsonElement?>? getModelSnapshot = null,
    Func<bool, CancellationToken, Task>? persistAutoCompactionEnabled = null,
    Func<ModelDescriptor, JsonElement>? projectModel = null,
    Func<string, CancellationToken, Task>? validateSwitchSession = null,
    Func<ConversationSession, CancellationToken, Task<string?>>? prepareCloneSession = null,
    Func<string?, CancellationToken, Task<IReadOnlyList<ModelDescriptor>>>? discoverCycleModels = null)
{
    private readonly JsonLineWriter _writer = new(output);
    private RpcEventWriter? _events;
    private readonly ConcurrentDictionary<Guid, BashOperation> _bashOperations = new();
    private readonly ConcurrentDictionary<Guid, RpcCompactionOperation> _compactionOperations = new();
    private CancellationTokenSource? _abort;
    private Task? _active;
    private ConversationRun CurrentRun => getCurrentRun?.Invoke() ?? run;
    private PiSharp.Runtime.Resources.ResourceCatalog? CurrentResources => getCurrentResources?.Invoke() ?? resources;
    private ExtensionRegistration? CurrentExtensions => getCurrentExtensions?.Invoke() ?? extensions;
    private RpcEventWriter Events => _events ??= new RpcEventWriter(_writer);

    public async Task ServeAsync(CancellationToken cancellationToken = default)
    {
        var modelCommands = new RpcModelCommandHandler(_writer, RespondAsync, () => Events,
            discoverModels, discoverCycleModels ?? discoverModels, setModel,
            projectModel ?? RpcModelProjector.Project, () => CurrentRun, getThinkingLevel, isModelScoped,
            setThinkingLevel, setThinkingLevelDuringRun, getAvailableThinkingLevels, supportsThinking);
        var retryCommands = new RpcRetryCommandHandler(RespondAsync, () => CurrentRun, persistRetryEnabled);
        var compactionCommands = new RpcCompactionCommandHandler(_writer, RespondAsync, () => CurrentRun,
            persistAutoCompactionEnabled, CancelActivePromptAsync,
            item => Events.EmitCommandLifecycleAsync(CurrentRun, item));
        var queueModeCommands = new RpcQueueModeCommandHandler(RespondAsync, () => CurrentRun, persistQueueMode);
        var stateCommands = new RpcStateCommandHandler(_writer, () => CurrentRun,
            () => getThinkingLevel?.Invoke(), () => _active is { IsCompleted: false },
            () => getModelSnapshot?.Invoke());
        var sessionCommands = new RpcSessionCommandHandler(_writer, RespondAsync, () => CurrentRun,
            () => _active is { IsCompleted: false }, () => Events, save,
            newSession is null ? null : StartNewSessionAsync,
            forkSession is null ? null : StartForkSessionAsync,
            cloneSession is null ? null : StartCloneSessionAsync,
            switchSession is null ? null : StartSwitchSessionAsync);
        var commandDiscoveryCommands = new RpcCommandDiscoveryHandler(_writer, () => CurrentResources, () => CurrentExtensions);
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
                    if (type == "compact")
                    {
                        StartCompaction(compactionCommands, root.Clone(), id, cancellationToken);
                        continue;
                    }
                    var busy = _active is { IsCompleted: false };
                    if (await stateCommands.TryHandleAsync(type, id, cancellationToken)) continue;
                    if (await compactionCommands.TryHandleAsync(type, root, id, cancellationToken)) continue;
                    if (await queueModeCommands.TryHandleAsync(type, root, id, cancellationToken)) continue;
                    if (await retryCommands.TryHandleAsync(type, root, id, cancellationToken)) continue;
                    if (await sessionCommands.TryHandleAsync(type, root, id, cancellationToken)) continue;
                    if (await modelCommands.TryHandleAsync(type, root, id, busy, cancellationToken)) continue;
                    if (await commandDiscoveryCommands.TryHandleAsync(type, id, cancellationToken)) continue;
                    switch (type)
                    {
                        case "export_html":
                            if (busy) { await RespondAsync(id, type, false, "Wait until the active prompt settles."); break; }
                            if (root.TryGetProperty("outputPath", out var outputPath) &&
                                (outputPath.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(outputPath.GetString())))
                            { await RespondAsync(id, type, false, "outputPath must be a nonempty string."); break; }
                            try
                            {
                                var path = Path.GetFullPath(root.TryGetProperty("outputPath", out outputPath)
                                    ? outputPath.GetString()! : Path.Combine(CurrentRun.Conversation.WorkingDirectory,
                                        $"pisharp-{CurrentRun.Conversation.Id[..12]}.html"));
                                await SessionExport.ExportHtmlAsync(CurrentRun.Conversation, path, cancellationToken);
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
                                data = SessionStatistics.Calculate(CurrentRun.Conversation)
                            }, cancellationToken);
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
                                var activeResources = CurrentResources;
                                expanded = activeResources is null ? message.GetString()! :
                                await activeResources.ResolveInputAsync(message.GetString()!, cancellationToken);
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
                                var currentRun = CurrentRun;
                                currentRun.QueueRpcInput(expanded, promptStreamingBehavior == "steer",
                                    item => PublishQueueUpdate(currentRun, item));
                                await RespondPromptAsync(id, true, "queued");
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
                            CurrentRun.AbortBash();
                            foreach (var operation in _bashOperations.Values)
                            {
                                try { operation.Cancellation.Cancel(); }
                                catch (ObjectDisposedException) { }
                            }
                            await RespondAsync(id, type, true);
                            break;
                        case "steer":
                        case "follow_up":
                            if (!TryGetTextMessage(root, out var queuedMessage, out var queueError))
                            { await RespondAsync(id, type, false, queueError); break; }
                            string queuedExpanded;
                            try
                            {
                                var activeResources = CurrentResources;
                                queuedExpanded = activeResources is null ? queuedMessage! :
                                    await activeResources.ResolveInputAsync(queuedMessage!, cancellationToken);
                            }
                            catch (Exception error) when (error is ArgumentException or IOException)
                            { await RespondAsync(id, type, false, error.Message); break; }
                            var queueRun = CurrentRun;
                            queueRun.QueueRpcInput(queuedExpanded, type == "steer",
                                item => PublishQueueUpdate(queueRun, item));
                            await RespondQueuedInputAsync(id, type, true);
                            break;
                        case "clear_queue":
                            var clearRun = CurrentRun;
                            var pending = clearRun.ClearPendingPrompts(item => PublishQueueUpdate(clearRun, item));
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
                            if (busy) await CancelActivePromptAsync();
                            await CancelActiveCompactionsAsync();
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
            var compactions = _compactionOperations.Values.ToArray();
            if (compactions.Length > 0)
                await Task.WhenAll(compactions.Select(operation => operation.Completed.Task));
            _abort?.Dispose();
        }
    }

    private void StartCompaction(RpcCompactionCommandHandler handler, JsonElement root, JsonElement? id,
        CancellationToken cancellationToken)
    {
        var key = Guid.NewGuid();
        var operation = new RpcCompactionOperation(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        if (!_compactionOperations.TryAdd(key, operation))
        {
            operation.Cancellation.Dispose();
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await handler.TryHandleAsync("compact", root, id, operation.Cancellation.Token,
                    () => CancelActiveRunAsync(key));
            }
            catch (Exception error)
            {
                await RespondAsync(id, "compact", false, error.Message);
            }
            finally
            {
                _compactionOperations.TryRemove(key, out _);
                operation.Cancellation.Dispose();
                operation.Completed.TrySetResult();
            }
        }, CancellationToken.None);
    }

    private async Task<bool> StartNewSessionAsync(string? parentSession, CancellationToken cancellationToken)
    {
        await CancelActiveRunAsync();
        return await newSession!(parentSession, cancellationToken);
    }

    private async Task<string?> StartForkSessionAsync(string entryId, CancellationToken cancellationToken)
    {
        var current = CurrentRun;
        var (branchSnapshot, selectedText) = current.Conversation.ForkAtUser(entryId, current.SessionFile);
        await CancelActiveRunAsync();
        return await forkSession!(entryId, branchSnapshot, selectedText, cancellationToken);
    }

    private async Task<bool> StartCloneSessionAsync(CancellationToken cancellationToken)
    {
        var current = CurrentRun;
        var branchSnapshot = current.Conversation.ForkForSessionReplacement(current.SessionFile);
        var branchPath = prepareCloneSession is null ? null :
            await prepareCloneSession(branchSnapshot, cancellationToken);
        await CancelActiveRunAsync();
        return await cloneSession!(branchSnapshot, branchPath, cancellationToken);
    }

    private async Task<bool> StartSwitchSessionAsync(string sessionPath, CancellationToken cancellationToken)
    {
        if (validateSwitchSession is not null)
            await validateSwitchSession(sessionPath, cancellationToken);
        await CancelActiveRunAsync();
        return await switchSession!(sessionPath, cancellationToken);
    }

    private async Task CancelActiveRunAsync(Guid? exceptCompaction = null)
    {
        await CancelActivePromptAsync();
        await CancelActiveCompactionsAsync(exceptCompaction);
    }

    private async Task CancelActivePromptAsync()
    {
        if (_active is not { IsCompleted: false } active) return;
        _abort?.Cancel();
        try { await active; }
        catch (OperationCanceledException) { }
    }

    private async Task CancelActiveCompactionsAsync(Guid? exceptOperation = null)
    {
        var active = _compactionOperations.Where(item => item.Key != exceptOperation)
            .Select(item => item.Value).ToArray();
        foreach (var operation in active)
        {
            try { operation.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        if (active.Length > 0) await Task.WhenAll(active.Select(operation => operation.Completed.Task));
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

    private async Task ExecuteAsync(JsonElement? id, string message, CancellationToken token)
    {
        var responded = false;
        try
        {
            await Events.RunAsync(CurrentRun, message, token, async item =>
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
            }, getApi?.Invoke());
            if (save is not null) await save(CancellationToken.None);
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        {
            if (!responded) await RespondPromptAsync(id, false, error: error.Message);
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
            var recordedNow = CurrentRun.RecordBashResult(command, result, excludeFromContext);
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
        var activeExtensions = CurrentExtensions;
        if (activeExtensions is not null)
        {
            var context = new UserBashContext(command, excludeFromContext, CurrentRun.Conversation.WorkingDirectory,
                delta => EmitBashUpdateAsync(correlationId, delta));
            foreach (var handler in activeExtensions.UserBashHandlers)
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

        return await CurrentRun.ExecuteBashAsync(command,
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

    private sealed class RpcCompactionOperation(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private Task RespondAsync(JsonElement? id, string command, bool success, string? error = null) => success
        ? _writer.EmitAsync(new { id, type = "response", command, success = true })
        : _writer.EmitAsync(new { id, type = "response", command, success = false, error });

    private void PublishQueueUpdate(ConversationRun currentRun, AgentLifecycleEvent item) =>
        Events.EmitCommandLifecycleAsync(currentRun, item).GetAwaiter().GetResult();

    private Task RespondQueuedInputAsync(JsonElement? id, string command, bool success, string? error = null) =>
        success
            ? _writer.EmitAsync(new { id, type = "response", command, success = true, data = new { disposition = "queued" } })
            : RespondAsync(id, command, false, error);

    private Task RespondPromptAsync(JsonElement? id, bool success, string? disposition = null, string? error = null) =>
        success
            ? _writer.EmitAsync(new { id, type = "response", command = "prompt", success = true, data = new { disposition } })
            : RespondAsync(id, "prompt", false, error);
}
