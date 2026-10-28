using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Tools;
using PiSharp.Cli.Tui;
using PublicExtensionUi = PiSharp.Extensions.Abstractions.IPiSharpExtensionUi;

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
    Func<string?, CancellationToken, Task<IReadOnlyList<ModelDescriptor>>>? discoverCycleModels = null,
    Func<JsonObject?>? getSystemMessage = null,
    Func<bool>? getProjectTrusted = null,
    string extensionMode = "rpc",
    bool extensionHasUserInterface = false,
    PublicExtensionUi? extensionUi = null,
    Func<Action, Func<CancellationToken, Task>, Action, PiSharpExtensionCommandControlOperations>? createCommandControlOperations = null,
    Func<string, PiSharpExtensionSettingsFlagContext>? createSettingsFlagContext = null,
    Func<ModelDescriptor, CancellationToken, Task<ModelDescriptor>>? cycleModel = null,
    Action<Func<ConversationRun,
        Func<string?, PiSharpExtensionSessionContext?, PiSharpExtensionCapabilities?>>>? installExtensionCapabilitiesFactory = null,
    Func<string?>? getSystemPrompt = null)
{
    private const int MaxRpcRecordLength = 16 * 1024 * 1024;
    private readonly JsonLineWriter _writer = new(output);
    private RpcEventWriter? _events;
    private readonly ConcurrentDictionary<Guid, BashOperation> _bashOperations = new();
    private readonly ConcurrentDictionary<Guid, RpcCompactionOperation> _compactionOperations = new();
    private CancellationTokenSource? _abort;
    private CancellationTokenSource? _serveCancellation;
    private Task? _active;
    private int _activeSettled;
    private int _shutdownRequested;
    internal Func<CancellationToken, Task>? AfterServeBeforeUiShutdown { get; set; }
    private bool IsActive => _active is { IsCompleted: false } && Volatile.Read(ref _activeSettled) == 0;
    private ConversationRun CurrentRun => getCurrentRun?.Invoke() ?? run;
    private PiSharp.Runtime.Resources.ResourceCatalog? CurrentResources => getCurrentResources?.Invoke() ?? resources;
    private ExtensionRegistration? CurrentExtensions => getCurrentExtensions?.Invoke() ?? extensions;
    private RpcEventWriter Events => _events ??= new RpcEventWriter(_writer);

    public async Task ServeAsync(CancellationToken cancellationToken = default)
    {
        using var serveCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _serveCancellation = serveCancellation;
        RpcExtensionUiTransport? rpcUiTransport = null;
        var startupUi = extensionUi as ProjectTrustStartupUi;
        installExtensionCapabilitiesFactory?.Invoke(activeRun => (sourcePath, session) =>
            CreateExtensionCapabilities(activeRun, sourcePath, session));
        var modelCommands = new RpcModelCommandHandler(_writer, RespondAsync, () => Events,
            discoverModels, discoverCycleModels ?? discoverModels, setModel, cycleModel,
            projectModel ?? RpcModelProjector.Project, () => CurrentRun, getThinkingLevel, isModelScoped,
            setThinkingLevel, setThinkingLevelDuringRun, getAvailableThinkingLevels, supportsThinking);
        var retryCommands = new RpcRetryCommandHandler(RespondAsync, () => CurrentRun, persistRetryEnabled);
        var compactionCommands = new RpcCompactionCommandHandler(_writer, RespondAsync, () => CurrentRun,
            persistAutoCompactionEnabled, CancelActivePromptAsync,
            item => Events.EmitCommandLifecycleAsync(CurrentRun, item));
        var queueModeCommands = new RpcQueueModeCommandHandler(RespondAsync, () => CurrentRun, persistQueueMode);
        var stateCommands = new RpcStateCommandHandler(_writer, () => CurrentRun,
            () => getThinkingLevel?.Invoke(), () => IsActive,
            () => getModelSnapshot?.Invoke(),
            () => Events.ProjectMessagesSnapshot(CurrentRun.Conversation, getSystemMessage?.Invoke()).Count);
        var sessionCommands = new RpcSessionCommandHandler(_writer, RespondAsync, () => CurrentRun,
            () => IsActive, () => Events,
            newSession is null ? null : StartNewSessionAsync,
            forkSession is null ? null : StartForkSessionAsync,
            cloneSession is null ? null : StartCloneSessionAsync,
            switchSession is null ? null : StartSwitchSessionAsync,
            getSystemMessage, getApi, save);
        var commandDiscoveryCommands = new RpcCommandDiscoveryHandler(_writer, () => CurrentResources, () => CurrentExtensions);
        var promptCommandHandler = new RpcPromptCommandHandler(_writer, () => CurrentExtensions,
            CreateExtensionCommandContext);
        try
        {
            if (startupUi is not null)
            {
                rpcUiTransport = new(_writer);
                startupUi.BindRpcUiTransport(rpcUiTransport);
                await rpcUiTransport.FlushAsync();
            }
            while (await input.ReadLineAsync(serveCancellation.Token) is { } line)
            {
                if (line.Length > MaxRpcRecordLength)
                {
                    await RespondAsync(null, "parse", false, "Command exceeds the 16 MiB RPC record limit.");
                    continue;
                }
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException error)
                {
                    await RespondAsync(null, "parse", false, $"Failed to parse command: {error.Message}");
                    continue;
                }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String)
                    {
                        await RespondAsync(null, "unknown", false, "Command must be an object with a type.");
                        continue;
                    }
                    var type = kind.GetString()!;
                    if (type == "extension_ui_response")
                    {
                        rpcUiTransport?.ReceiveResponse(root);
                        continue;
                    }
                    var id = root.TryGetProperty("id", out var requestId) ? requestId.Clone() : (JsonElement?)null;
                    if (type == "compact")
                    {
                        StartCompaction(compactionCommands, root.Clone(), id, cancellationToken);
                        continue;
                    }
                    var busy = IsActive;
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
                            if (root.TryGetProperty("outputPath", out var outputPath) &&
                                (outputPath.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(outputPath.GetString())))
                            { await RespondAsync(id, type, false, "outputPath must be a nonempty string."); break; }
                            try
                            {
                                var path = Path.GetFullPath(root.TryGetProperty("outputPath", out outputPath)
                                    ? outputPath.GetString()! : Path.Combine(CurrentRun.Conversation.WorkingDirectory,
                                        $"pisharp-{CurrentRun.Conversation.Id[..12]}.html"));
                                var snapshot = CurrentRun.Conversation.Snapshot();
                                await SessionExport.ExportHtmlAsync(snapshot, path, cancellationToken);
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
                            var physicalContext = await CurrentRun.GetPhysicalContextAsync(cancellationToken);
                            var contextModel = physicalContext is not null && projectModel is not null
                                ? projectModel(physicalContext.Model) : getModelSnapshot?.Invoke();
                            await _writer.EmitAsync(new
                            {
                                id,
                                type = "response",
                                command = type,
                                success = true,
                                data = RpcSessionStatsProjector.Project(CurrentRun.Conversation, CurrentRun.SessionFile,
                                    contextModel, getSystemMessage?.Invoke())
                            }, cancellationToken);
                            break;
                        case "prompt":
                            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String ||
                                string.IsNullOrWhiteSpace(message.GetString()))
                            { await RespondAsync(id, type, false, "A nonempty text message is required."); break; }
                            if (promptCommandHandler.TryStart(message.GetString()!, id, cancellationToken)) break;
                            if (!TryGetImages(root, out var promptInputImages, out var imageError))
                            { await RespondAsync(id, type, false, imageError); break; }
                            string? promptStreamingBehavior = null;
                            if (root.TryGetProperty("streamingBehavior", out var requestedBehavior))
                            {
                                if (requestedBehavior.ValueKind != JsonValueKind.String ||
                                    requestedBehavior.GetString() is not ("steer" or "followUp"))
                                { await RespondAsync(id, type, false, "streamingBehavior must be 'steer' or 'followUp'."); break; }
                                promptStreamingBehavior = requestedBehavior.GetString();
                            }
                            var promptBehavior = busy ? promptStreamingBehavior switch
                            {
                                "steer" => PiSharpInputStreamingBehavior.Steer,
                                "followUp" => PiSharpInputStreamingBehavior.FollowUp,
                                _ => (PiSharpInputStreamingBehavior?)null
                            } : null;
                            var currentRun = CurrentRun;
                            var intercepted = await currentRun.InterceptInputAsync(message.GetString()!, promptInputImages,
                                PiSharpInputSource.Rpc, promptBehavior, cancellationToken);
                            if (intercepted.Action == PiSharpInputResultAction.Handled)
                            {
                                await RespondPromptAsync(id, true, "handled");
                                break;
                            }
                            var promptText = intercepted.Action == PiSharpInputResultAction.Transform
                                ? intercepted.Text! : message.GetString()!;
                            var promptImages = intercepted.Action == PiSharpInputResultAction.Transform
                                ? intercepted.Images : promptInputImages;
                            string expanded;
                            try
                            {
                                var activeResources = CurrentResources;
                                expanded = activeResources is null ? promptText :
                                await activeResources.ResolveInputAsync(promptText, cancellationToken, ReportResourceErrorAsync);
                            }
                            catch (Exception error) when (error is ArgumentException or IOException)
                            { await RespondAsync(id, type, false, error.Message); break; }
                            var promptContents = new List<AIContent> { new TextContent(expanded) };
                            if (promptImages is not null) promptContents.AddRange(promptImages);
                            var promptMessage = new ChatMessage(ChatRole.User, promptContents);
                            if (busy)
                            {
                                if (promptStreamingBehavior is null)
                                {
                                    await RespondAsync(id, type, false,
                                        "The active run requires streamingBehavior 'steer' or 'followUp'.");
                                    break;
                                }
                                currentRun.QueueRpcInput(promptMessage, expanded, promptStreamingBehavior == "steer",
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
                            if (_active is { IsCompleted: false } finishing) await finishing;
                            _abort?.Dispose();
                            _abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            Volatile.Write(ref _activeSettled, 0);
                            _active = ExecuteAsync(id, promptMessage, expanded, _abort.Token);
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
                            if (!TryGetImages(root, out var queueInputImages, out var queueImageError))
                            { await RespondAsync(id, type, false, queueImageError); break; }
                            if (TryGetExtensionCommandName(queuedMessage!, CurrentExtensions, out var commandName))
                            {
                                await RespondAsync(id, type, false,
                                    $"Extension command \"/{commandName}\" cannot be queued. Use prompt or execute the command when not streaming.");
                                break;
                            }
                            var queueRun = CurrentRun;
                            var queueBehavior = busy
                                ? type == "steer" ? PiSharpInputStreamingBehavior.Steer : PiSharpInputStreamingBehavior.FollowUp
                                : (PiSharpInputStreamingBehavior?)null;
                            var queuedInput = await queueRun.InterceptInputAsync(queuedMessage!, queueInputImages,
                                PiSharpInputSource.Rpc, queueBehavior, cancellationToken);
                            if (queuedInput.Action == PiSharpInputResultAction.Handled)
                            {
                                await RespondQueuedInputAsync(id, type, true, disposition: "handled");
                                break;
                            }
                            var queueText = queuedInput.Action == PiSharpInputResultAction.Transform
                                ? queuedInput.Text! : queuedMessage!;
                            var queueImages = queuedInput.Action == PiSharpInputResultAction.Transform
                                ? queuedInput.Images : queueInputImages;
                            string queuedExpanded;
                            try
                            {
                                var activeResources = CurrentResources;
                                queuedExpanded = activeResources is null ? queueText :
                                    await activeResources.ResolveInputAsync(queueText, cancellationToken, ReportResourceErrorAsync);
                            }
                            catch (Exception error) when (error is ArgumentException or IOException)
                            { await RespondAsync(id, type, false, error.Message); break; }
                            var queueContents = new List<AIContent> { new TextContent(queuedExpanded) };
                            if (queueImages is not null) queueContents.AddRange(queueImages);
                            queueRun.QueueRpcInput(new ChatMessage(ChatRole.User, queueContents), queuedExpanded, type == "steer",
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
        catch (OperationCanceledException) when (Volatile.Read(ref _shutdownRequested) != 0 &&
            !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await promptCommandHandler.CancelAndWaitAsync();
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
            rpcUiTransport?.StopDialogs();
            try
            {
                if (AfterServeBeforeUiShutdown is not null)
                    await AfterServeBeforeUiShutdown(CancellationToken.None);
            }
            finally
            {
                if (rpcUiTransport is not null)
                {
                    startupUi?.UnbindRpcUiTransport(rpcUiTransport);
                    await rpcUiTransport.DisposeAsync();
                }
                _serveCancellation = null;
            }
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
        var current = CurrentRun;
        var beforeSwitch = await current.DispatchSessionBeforeSwitchAsync(
            PiSharpExtensionSessionSwitchReason.New, null, current.SessionFile, cancellationToken);
        if (beforeSwitch?.Cancel == true)
            throw new InvalidOperationException("Session transition was cancelled by an extension.");
        await CancelActiveRunAsync();
        return await newSession!(parentSession, cancellationToken);
    }

    private async Task<string?> StartForkSessionAsync(string entryId, CancellationToken cancellationToken)
    {
        var current = CurrentRun;
        var (branchSnapshot, selectedText) = current.Conversation.ForkAtUser(entryId, current.SessionFile);
        var beforeFork = await current.DispatchSessionBeforeForkAsync(entryId,
            PiSharpExtensionForkPosition.Before, current.SessionFile, cancellationToken);
        if (beforeFork?.Cancel == true)
            throw new InvalidOperationException("Session fork was cancelled by an extension.");
        await CancelActiveRunAsync();
        return await forkSession!(entryId, branchSnapshot, selectedText, cancellationToken);
    }

    private async Task<bool> StartCloneSessionAsync(CancellationToken cancellationToken)
    {
        var current = CurrentRun;
        var entryId = current.Conversation.Tree.HeadId;
        if (entryId is not null)
        {
            var beforeFork = await current.DispatchSessionBeforeForkAsync(entryId,
                PiSharpExtensionForkPosition.At, current.SessionFile, cancellationToken);
            if (beforeFork?.Cancel == true)
                throw new InvalidOperationException("Session fork was cancelled by an extension.");
        }
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
        var current = CurrentRun;
        var targetSessionFile = Path.GetFullPath(sessionPath, current.Conversation.WorkingDirectory);
        var beforeSwitch = await current.DispatchSessionBeforeSwitchAsync(
            PiSharpExtensionSessionSwitchReason.Resume, targetSessionFile, current.SessionFile,
            cancellationToken);
        if (beforeSwitch?.Cancel == true)
            throw new InvalidOperationException("Session transition was cancelled by an extension.");
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
            string.IsNullOrWhiteSpace(message = value.GetString()))
        {
            error = "A nonempty text message is required.";
            return false;
        }
        return true;
    }

    private static bool TryGetImages(JsonElement root, out IReadOnlyList<DataContent>? images, out string? error)
    {
        images = null;
        error = null;
        if (!root.TryGetProperty("images", out var value)) return true;
        if (value.ValueKind != JsonValueKind.Array)
        {
            error = "images must be an array.";
            return false;
        }

        var parsed = new List<DataContent>();
        var index = 0;
        foreach (var image in value.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object ||
                !image.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                type.GetString() != "image")
            {
                error = $"images[{index}] must have type 'image'.";
                return false;
            }
            if (!image.TryGetProperty("mimeType", out var mimeType) || mimeType.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(mimeType.GetString()) ||
                !mimeType.GetString()!.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                error = $"images[{index}].mimeType must be an image media type.";
                return false;
            }
            if (!image.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)
            {
                error = $"images[{index}].data must be a base64 string.";
                return false;
            }

            byte[] bytes;
            try { bytes = Convert.FromBase64String(data.GetString()!); }
            catch (FormatException)
            {
                error = $"images[{index}].data must be valid base64.";
                return false;
            }
            parsed.Add(new DataContent(bytes, mimeType.GetString()!));
            index++;
        }

        images = parsed;
        return true;
    }

    private static bool TryGetExtensionCommandName(string text, ExtensionRegistration? extensions, out string name)
    {
        name = "";
        if (extensions is null || text.Length == 0 || text[0] != '/') return false;
        var separator = text.IndexOf(' ');
        name = separator < 0 ? text[1..] : text[1..separator];
        return name.Length > 0 &&
            (extensions.Commands.ContainsKey(name) || extensions.ContextCommands.ContainsKey(name));
    }

    private async Task ExecuteAsync(JsonElement? id, ChatMessage promptMessage, string promptText, CancellationToken token)
    {
        var responded = false;
        try
        {
            var currentRun = CurrentRun;
            await Events.RunEventsAsync(currentRun,
                currentRun.RunUserMessageEventsAsync(promptMessage, promptText, token), token, async item =>
            {
                if (item.Type == "agent_settled")
                {
                    if (save is not null)
                    {
                        try { await save(CancellationToken.None); }
                        catch (Exception error)
                        { await _writer.EmitAsync(new { type = "error", error = error.Message }, CancellationToken.None); }
                    }
                    Volatile.Write(ref _activeSettled, 1);
                    return;
                }
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

    private PiSharpExtensionCommandContext CreateExtensionCommandContext(string commandName,
        ExtensionCommandInfo commandInfo)
    {
        var run = CurrentRun;
        var controls = CreateExtensionControls();
        var session = run.CreateExtensionSessionContext(commandInfo.SourceInfo.Path);
        var settingsAndFlags = createSettingsFlagContext?.Invoke(commandInfo.SourceInfo.Path);
        var sendUserMessageAsync = CreateExtensionUserMessageSender();
        var sendCustomMessageAsync = CreateExtensionCustomMessageSender(commandInfo.SourceInfo.Path);
        var capabilities = CreateExtensionCapabilities(run, commandInfo.SourceInfo.Path, session,
            controls, sendUserMessageAsync, sendCustomMessageAsync, settingsAndFlags);
        var callbackUi = extensionUi is not null && CurrentExtensions is { } currentExtensions
            ? currentExtensions.CreateCallbackUi(commandInfo.SourceInfo.Path, extensionUi)
            : extensionUi;

        var workingDirectory = run.Conversation.WorkingDirectory;
        var isProjectTrusted = getProjectTrusted?.Invoke() ?? false;
        if (controls is null)
        {
            return new PiSharpExtensionCommandContext(workingDirectory, !IsActive, isProjectTrusted,
                run.PendingPromptCount > 0, sendUserMessageAsync, sendCustomMessageAsync,
                extensionMode, extensionHasUserInterface, callbackUi)
            {
                Session = session,
                SettingsAndFlags = settingsAndFlags,
                Capabilities = capabilities
            };
        }

        return new PiSharpExtensionCommandContext(workingDirectory, !IsActive, isProjectTrusted,
            run.PendingPromptCount > 0, sendUserMessageAsync, sendCustomMessageAsync,
            extensionMode, extensionHasUserInterface, callbackUi, controls)
        {
            Session = session,
            SettingsAndFlags = settingsAndFlags,
            Capabilities = capabilities
        };
    }

    private PiSharpExtensionCommandControlOperations? CreateExtensionControls() =>
        createCommandControlOperations?.Invoke(() =>
        {
            try { _abort?.Cancel(); }
            catch (ObjectDisposedException) { }
        }, token => token.IsCancellationRequested ? Task.FromCanceled(token) : CancelActivePromptAsync(),
            RequestShutdown);

    private Func<string, CancellationToken, Task> CreateExtensionUserMessageSender() =>
        async (prompt, cancellationToken) =>
        {
            var currentRun = CurrentRun;
            PiSharpInputStreamingBehavior? behavior = currentRun.IsActive
                ? PiSharpInputStreamingBehavior.Steer : null;
            var intercepted = await currentRun.InterceptInputAsync(prompt, null, PiSharpInputSource.Extension,
                behavior, cancellationToken);
            if (intercepted.Action == PiSharpInputResultAction.Handled) return;
            var inputText = intercepted.Action == PiSharpInputResultAction.Transform ? intercepted.Text! : prompt;
            var images = intercepted.Action == PiSharpInputResultAction.Transform ? intercepted.Images : null;
            var expanded = CurrentResources is { } activeResources
                ? await activeResources.ResolveInputAsync(inputText, cancellationToken, ReportResourceErrorAsync)
                : inputText;
            var contents = new List<AIContent> { new TextContent(expanded) };
            if (images is not null) contents.AddRange(images);
            var message = new ChatMessage(ChatRole.User, contents);
            if (currentRun.IsActive)
            {
                if (!currentRun.TryQueueExtensionUserMessage(message, expanded))
                    throw new InvalidOperationException("The active conversation run settled before the message could be queued.");
                return;
            }
            var preflightError = promptPreflight?.Invoke();
            if (!string.IsNullOrWhiteSpace(preflightError)) throw new InvalidOperationException(preflightError);
            await StartExtensionRunAsync(token => currentRun.RunUserMessageEventsAsync(message, expanded, token), cancellationToken);
        };

    private Func<PiSharp.Extensions.Abstractions.PiSharpCustomMessage, bool,
        PiSharpMessageDeliveryMode, CancellationToken, Task> CreateExtensionCustomMessageSender(string? sourcePath) =>
        async (message, triggerTurn, deliveryMode, cancellationToken) =>
        {
            var currentRun = CurrentRun;
            if (!await currentRun.DeliverExtensionCustomMessageAsync(message, sourcePath,
                triggerTurn, deliveryMode, cancellationToken)) return;
            await StartExtensionRunAsync(token => currentRun.RunCustomMessageEventsAsync(message,
                sourcePath, token), cancellationToken);
        };

    private PiSharpExtensionCapabilities CreateExtensionCapabilities(ConversationRun run, string? sourcePath,
        PiSharpExtensionSessionContext? session,
        PiSharpExtensionCommandControlOperations? controls = null,
        Func<string, CancellationToken, Task>? sendUserMessageAsync = null,
        Func<PiSharp.Extensions.Abstractions.PiSharpCustomMessage, bool,
            PiSharpMessageDeliveryMode, CancellationToken, Task>? sendCustomMessageAsync = null,
        PiSharpExtensionSettingsFlagContext? settingsAndFlags = null)
    {
        controls ??= CreateExtensionControls();
        settingsAndFlags ??= string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : createSettingsFlagContext?.Invoke(sourcePath);
        sendUserMessageAsync ??= CreateExtensionUserMessageSender();
        sendCustomMessageAsync ??= CreateExtensionCustomMessageSender(sourcePath);
        return new PiSharpExtensionCapabilities(controls, session, settingsAndFlags,
            () =>
            {
                var window = run.ContextWindowTokens;
                if (window is not > 0) return null;
                var tokens = run.EstimatedContextTokens;
                double? percent = tokens is null ? null : Math.Clamp((double)tokens.Value / window.Value * 100, 0, 100);
                return new PiSharpExtensionContextUsage(tokens, window.Value, percent);
            }, getSystemPrompt, sendUserMessageAsync, sendCustomMessageAsync,
            canCompact: () => run.CanCompactFromExtensionCallback);
    }

    private void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return;
        try { _serveCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task StartExtensionRunAsync(
        Func<CancellationToken, IAsyncEnumerable<AgentLifecycleEvent>> getEvents,
        CancellationToken cancellationToken)
    {
        if (_active is { IsCompleted: false } finishing) await finishing.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _abort?.Dispose();
        _abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Volatile.Write(ref _activeSettled, 0);
        _active = RunExtensionEventsAsync(getEvents, _abort.Token);
        await _active;
    }

    private async Task RunExtensionEventsAsync(
        Func<CancellationToken, IAsyncEnumerable<AgentLifecycleEvent>> getEvents, CancellationToken cancellationToken)
    {
        try
        {
            await Events.RunEventsAsync(CurrentRun, getEvents(cancellationToken), cancellationToken, async item =>
            {
                if (item.Type != "agent_settled") return;
                if (save is not null)
                {
                    try { await save(CancellationToken.None); }
                    catch (Exception error)
                    { await _writer.EmitAsync(new { type = "error", error = error.Message }, CancellationToken.None); }
                }
                Volatile.Write(ref _activeSettled, 1);
            }, getApi?.Invoke());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            await _writer.EmitAsync(new { type = "error", error = error.Message }, CancellationToken.None);
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
            BashExecutionResult result;
            try
            {
                result = await ExecuteUserBashAsync(correlationId, command, excludeFromContext,
                    operation.Cancellation.Token);
            }
            catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested)
            {
                result = new BashExecutionResult("", "(command cancelled)", null, true, false, null);
            }

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
        Task EmitUpdateAsync(string delta) => EmitBashUpdateAsync(correlationId, delta);
        var interception = await CurrentRun.InterceptUserBashAsync(command, excludeFromContext, EmitUpdateAsync,
            cancellationToken);
        if (interception is null)
        {
            return await CurrentRun.ExecuteBashAsync(command,
                delta => EmitBashUpdateAsync(correlationId, delta).GetAwaiter().GetResult(), cancellationToken);
        }

        if (interception.Kind == PiSharpUserBashResultKind.FullResult)
        {
            var output = interception.Output ?? "";
            return new BashExecutionResult(output, output, interception.ExitCode, interception.Cancelled,
                interception.Truncated, interception.FullOutputPath);
        }

        if (interception.Kind == PiSharpUserBashResultKind.CustomOperation &&
            interception.CustomOperation is { } customOperation)
        {
            return await CurrentRun.ExecuteUserBashOperationAsync(command, customOperation,
                delta => EmitBashUpdateAsync(correlationId, delta).GetAwaiter().GetResult(), cancellationToken);
        }

        throw new InvalidOperationException("The user_bash handler returned an invalid result.");
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

    private Task ReportResourceErrorAsync(PiSharp.Runtime.Resources.ResourceExpansionError error) =>
        _writer.EmitAsync(new
        {
            type = "extension_error",
            extensionPath = error.ExtensionPath,
            @event = error.Event,
            error = error.Error
        }, CancellationToken.None);

    private Task RespondAsync(JsonElement? id, string command, bool success, string? error = null) => success
        ? _writer.EmitAsync(new { id, type = "response", command, success = true })
        : _writer.EmitAsync(new { id, type = "response", command, success = false, error });

    private void PublishQueueUpdate(ConversationRun currentRun, AgentLifecycleEvent item) =>
        Events.EmitCommandLifecycleAsync(currentRun, item).GetAwaiter().GetResult();

    private Task RespondQueuedInputAsync(JsonElement? id, string command, bool success, string? error = null,
        string disposition = "queued") =>
        success
            ? _writer.EmitAsync(new { id, type = "response", command, success = true, data = new { disposition } })
            : RespondAsync(id, command, false, error);

    private Task RespondPromptAsync(JsonElement? id, bool success, string? disposition = null, string? error = null) =>
        success
            ? _writer.EmitAsync(new { id, type = "response", command = "prompt", success = true, data = new { disposition } })
            : RespondAsync(id, "prompt", false, error);
}
