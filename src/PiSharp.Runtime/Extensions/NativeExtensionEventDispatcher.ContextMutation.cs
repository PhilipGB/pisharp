using System.Text.Json;
using Microsoft.Extensions.AI;
using NativeExtensions = PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionEventDispatcher
{
    internal bool HasContextMutationHandlers => registration.ContextHandlers.Count > 0 ||
        registration.ContextWithSystemHandlers.Count > 0;

    internal async Task<NativeContextMutationResult> DispatchContextMutationsAsync(
        IReadOnlyList<ChatMessage> messages, string? systemInstructions, IReadOnlyList<AITool> tools,
        CancellationToken cancellationToken)
    {
        var current = messages.ToList();
        var messagesChanged = false;
        foreach (var handler in registration.ContextHandlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<ChatMessage> working;
            ChatMessage[] visible;
            try
            {
                working = PiSharpContextTransformPipeline.CloneMessagesForNativeContext(current).ToList();
                visible = working.Where(message => message.Role != ChatRole.System).ToArray();
            }
            catch (Exception error)
            {
                Report(error, handler.SourceInfo, "context", null);
                continue;
            }

            var handlerBaseline = PiSharpContextTransformPipeline.CloneMessagesForNativeContext(working);
            var eventMessages = visible.ToList();
            var eventData = new NativeExtensions.PiSharpContextEvent(eventMessages);
            var succeeded = await InvokeContextHandlerAsync(handler.Handler, eventData, handler.SourceInfo,
                cancellationToken).ConfigureAwait(false);
            if (!succeeded)
            {
                // Pi's filtered event list is a new array, but its message objects are shared
                // with the current request. Preserve valid in-place message edits while ignoring
                // list replacements performed by a failing handler.
                try
                {
                    var detachedVisible = PiSharpContextTransformPipeline.CloneMessagesForNativeContext(visible);
                    var priorSystems = working.Where(message => message.Role == ChatRole.System).ToArray();
                    current = MergeByOriginalPositions(working, detachedVisible, priorSystems);
                }
                catch (Exception error)
                {
                    Report(error, handler.SourceInfo, "context", null);
                }
                continue;
            }

            try
            {
                var replacement = ValidateAndClone(eventData.Messages);
                var listChanged = !SameReferences(visible, eventData.Messages);
                if (listChanged)
                {
                    var restoredSystem = SystemMessageTranscript.ReplayForContext(working);
                    current = restoredSystem is null ? replacement.ToList() : [restoredSystem, .. replacement];
                    messagesChanged = true;
                }
                else
                {
                    current = MergeByOriginalPositions(working,
                        PiSharpContextTransformPipeline.CloneMessagesForNativeContext(visible),
                        working.Where(message => message.Role == ChatRole.System).ToArray());
                }
            }
            catch (Exception error)
            {
                Report(error, handler.SourceInfo, "context", null);
                current = handlerBaseline.ToList();
            }
        }

        foreach (var handler in registration.ContextWithSystemHandlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<ChatMessage> working;
            try { working = PiSharpContextTransformPipeline.CloneMessagesForNativeContext(current).ToList(); }
            catch (Exception error)
            {
                Report(error, handler.SourceInfo, "context_with_system", null);
                continue;
            }
            var beforeCallback = PiSharpContextTransformPipeline.CloneMessagesForNativeContext(working);
            var initialInstructions = systemInstructions;
            var initiallyLedWithSystem = working.FirstOrDefault()?.Role == ChatRole.System;
            var eventData = new NativeExtensions.PiSharpContextWithSystemEvent(working,
                systemInstructions, tools.Select(GetToolName).Where(name => name is not null).Cast<string>().ToArray());
            var succeeded = await InvokeContextWithSystemHandlerAsync(handler.Handler, eventData,
                handler.SourceInfo, cancellationToken).ConfigureAwait(false);
            if (!succeeded)
            {
                // The stage-two event shares its detached working list with the handler. Keep
                // valid in-place changes after a throw, as the upstream event runner does.
                try
                {
                    current = ValidateAndClone(working);
                    systemInstructions = eventData.SystemInstructions;
                    if (!SameMessages(beforeCallback, current) || !string.Equals(initialInstructions,
                            systemInstructions, StringComparison.Ordinal))
                        messagesChanged = true;
                    ReportLeadingSystemWarningIfNeeded(initiallyLedWithSystem, initialInstructions,
                        systemInstructions, current, handler.SourceInfo);
                }
                catch (Exception error)
                {
                    Report(error, handler.SourceInfo, "context_with_system", null);
                    current = PiSharpContextTransformPipeline.CloneMessagesForNativeContext(current).ToList();
                }
                continue;
            }

            try
            {
                current = ValidateAndClone(eventData.Messages);
                systemInstructions = eventData.SystemInstructions;
                if (!SameMessages(beforeCallback, current) || !string.Equals(initialInstructions,
                        systemInstructions, StringComparison.Ordinal))
                    messagesChanged = true;
                ReportLeadingSystemWarningIfNeeded(initiallyLedWithSystem, initialInstructions,
                    systemInstructions, current, handler.SourceInfo);
            }
            catch (Exception error)
            {
                Report(error, handler.SourceInfo, "context_with_system", null);
                current = beforeCallback.ToList();
            }
        }

        return new(current, systemInstructions, messagesChanged);
    }

    private async Task<bool> InvokeContextHandlerAsync(
        ExtensionEventHandler<NativeExtensions.PiSharpContextEvent> callback,
        NativeExtensions.PiSharpContextEvent eventData, ResourceSourceInfo sourceInfo,
        CancellationToken cancellationToken)
    {
        Task pending;
        try { pending = callback(eventData, context, cancellationToken).AsTask(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Report(error, sourceInfo, "context", null);
            return false;
        }

        try
        {
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await pending.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error) { Report(error, sourceInfo, "context", null); }
            throw;
        }
        catch (Exception error)
        {
            Report(error, sourceInfo, "context", null);
            return false;
        }
    }

    private async Task<bool> InvokeContextWithSystemHandlerAsync(
        ExtensionEventHandler<NativeExtensions.PiSharpContextWithSystemEvent> callback,
        NativeExtensions.PiSharpContextWithSystemEvent eventData, ResourceSourceInfo sourceInfo,
        CancellationToken cancellationToken)
    {
        Task pending;
        try { pending = callback(eventData, context, cancellationToken).AsTask(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            Report(error, sourceInfo, "context_with_system", null);
            return false;
        }

        try
        {
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await pending.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error) { Report(error, sourceInfo, "context_with_system", null); }
            throw;
        }
        catch (Exception error)
        {
            Report(error, sourceInfo, "context_with_system", null);
            return false;
        }
    }

    private static List<ChatMessage> ValidateAndClone(IList<ChatMessage>? messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Any(message => message is null))
            throw new InvalidDataException("Context messages cannot contain null entries.");
        return PiSharpContextTransformPipeline.CloneMessagesForNativeContext(messages).ToList();
    }

    private static bool SameReferences(IReadOnlyList<ChatMessage> left, IList<ChatMessage>? right) =>
        right is not null && left.Count == right.Count && left.Select((message, index) =>
            ReferenceEquals(message, right[index])).All(same => same);

    private static bool SameMessages(IReadOnlyList<ChatMessage> left, IReadOnlyList<ChatMessage> right)
    {
        if (left.Count != right.Count) return false;
        try
        {
            return JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(left, AIJsonUtilities.DefaultOptions),
                JsonSerializer.SerializeToElement(right, AIJsonUtilities.DefaultOptions));
        }
        catch (Exception) { return left.Zip(right).All(pair => ReferenceEquals(pair.First, pair.Second)); }
    }

    private static string? GetToolName(AITool tool) => tool switch
    {
        AIFunction function => function.Name,
        AIFunctionDeclaration declaration => declaration.Name,
        _ => null
    };

    private void ReportLeadingSystemWarningIfNeeded(bool initiallyLedWithSystem, string? initialInstructions,
        string? currentInstructions, IReadOnlyList<ChatMessage> messages, ResourceSourceInfo sourceInfo)
    {
        if (messages.FirstOrDefault()?.Role == ChatRole.System ||
            !initiallyLedWithSystem && (initialInstructions is null || currentInstructions is not null)) return;
        try
        {
            report(new AgentLifecycleEvent("extension_warning",
                Error: "context_with_system did not retain a leading system message.",
                Details: new { extensionPath = sourceInfo.Path, eventType = "context_with_system" }));
        }
        catch (Exception)
        {
            // Diagnostics must not replace the extension event's owning operation.
        }
    }

    private static List<ChatMessage> MergeByOriginalPositions(IReadOnlyList<ChatMessage> original,
        IReadOnlyList<ChatMessage> visible, IReadOnlyList<ChatMessage> systems)
    {
        var merged = new List<ChatMessage>(original.Count);
        var visibleIndex = 0;
        var systemIndex = 0;
        foreach (var message in original)
            merged.Add(message.Role == ChatRole.System ? systems[systemIndex++] : visible[visibleIndex++]);
        return merged;
    }
}

internal sealed record NativeContextMutationResult(IReadOnlyList<ChatMessage> Messages,
    string? SystemInstructions, bool MessagesChanged);
