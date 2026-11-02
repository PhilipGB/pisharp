using System.Runtime.ExceptionServices;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>Tool lifecycle originates here, at invocation time rather than from inferred model updates.</summary>
internal sealed class DurableToolFunction(AIFunction inner, Func<DurableExecution?> current,
    Action<AgentLifecycleEvent> publish, ToolBatchTermination termination, Func<ToolLoadout?>? currentLoadout = null,
    Func<IReadOnlyDictionary<string, AIFunction>>? allToolFunctions = null,
    PiSharpToolHookPipeline? toolHooks = null, JsonElement? outputSchema = null, ICodemodeModels? codemodeModels = null,
    Func<IDictionary<string, object?>, PiSharpToolReach?>? reachesTools = null,
    PiSharpToolNamespace? toolNamespace = null,
    Func<PiSharpExtensionContext?>? currentExtensionContext = null,
    NativeExtensionEventDispatcher? messageEvents = null, ToolCompletionGate? completionGate = null,
    ToolBatchSequencer? sequencing = null, Func<FunctionInvocationContext, bool>? batchRequiresSequential = null) : DelegatingAIFunction(inner)
{
    private readonly ToolResultSchemaValidator _outputValidator = new(outputSchema);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invocationArguments = new AIFunctionArguments(arguments.ToDictionary(pair => pair.Key, pair => pair.Value,
            StringComparer.Ordinal))
        {
            Context = arguments.Context
        };
        var loadout = currentLoadout?.Invoke();
        var snapshot = loadout?.Snapshot;
        var isCallable = snapshot is null || snapshot.ActiveToolNames.Contains(Name, StringComparer.Ordinal) ||
            snapshot.Callable.Any(tool => string.Equals(tool.Function.Name, Name, StringComparison.Ordinal));
        var nestedInvocation = PiSharpToolExecutionContext.GetNestedInvocation(arguments);
        var invocationContext = nestedInvocation is null ? FunctionInvokingChatClient.CurrentContext : null;
        using var sequenceScope = invocationContext is not null && sequencing is not null &&
            batchRequiresSequential?.Invoke(invocationContext) == true
            ? await sequencing.EnterAsync(invocationContext, true, cancellationToken)
            : null;
        var callContent = invocationContext?.CallContent;
        var toolCallId = nestedInvocation?.CallId ?? callContent?.CallId ?? Guid.NewGuid().ToString("N");
        var parentToolCallId = nestedInvocation?.ParentCallId;
        var callArguments = callContent is { Arguments: { } argumentsContent }
            ? argumentsContent.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : null;
        var execution = current();
        var id = execution is null ? Guid.NewGuid().ToString("N") :
            await execution.StartToolAsync(Name, arguments, cancellationToken);
        var displayArguments = invocationArguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        publish(new AgentLifecycleEvent("tool_execution_started", Tool: Name, OperationId: id)
        {
            ToolArguments = callArguments ?? displayArguments,
            ToolCallId = toolCallId,
            ParentToolCallId = parentToolCallId,
            NativeToolObserverOnly = false
        });
        PiSharpToolExecutionContext? toolExecutionContext = null;
        var toolStartedPublished = false;
        void PublishToolStarted()
        {
            if (toolStartedPublished) return;
            toolStartedPublished = true;
            displayArguments = invocationArguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            toolExecutionContext?.SetArguments(displayArguments);
            publish(new AgentLifecycleEvent("tool_execution_started", Tool: Name, OperationId: id)
            {
                ToolArguments = displayArguments,
                ToolCallId = toolCallId,
                ParentToolCallId = parentToolCallId,
                NativeToolObserverOnly = true
            });
        }
        if (loadout is not null)
        {
            Action<string>? onNestedUpdate = nestedInvocation?.OnUpdate is { } onUpdate
                ? text => onUpdate(new PiSharpToolExecutionResult(toolCallId, Name, null, text, false))
                : null;
            toolExecutionContext = nestedInvocation is null
                ? PiSharpToolExecutionContext.CreateRoot(loadout,
                    allToolFunctions ?? (() => new Dictionary<string, AIFunction>(StringComparer.Ordinal)),
                    publish, toolCallId, id, Name, callArguments ?? displayArguments, codemodeModels,
                    currentExtensionContext?.Invoke())
                : nestedInvocation.Parent.ForToolCall(toolCallId, parentToolCallId, id, Name,
                    callArguments ?? displayArguments, onNestedUpdate);
        }
        object? value = null;
        Exception? failure = null;
        long? startedAt = null;
        double? durationMs = null;
        IDictionary<object, object?>? argumentContext = null;
        object? previousUpdate = null;
        var hadUpdate = false;
        object? previousSnapshot = null;
        var hadSnapshot = false;
        object? previousPiSharpContext = null;
        var hadPiSharpContext = false;
        if (Name == "bash" || loadout is not null)
        {
            argumentContext = invocationArguments.Context ??= new Dictionary<object, object?>();
            if (Name == "bash")
            {
                hadUpdate = argumentContext.TryGetValue(CodingTools.BashOutputContextKey, out previousUpdate);
                argumentContext[CodingTools.BashOutputContextKey] = (Action<string>)(text =>
                {
                    toolExecutionContext?.ReportProgress(text);
                    if (toolExecutionContext is null)
                        publish(new AgentLifecycleEvent("tool_execution_update", Text: text, Tool: Name, OperationId: id)
                        {
                            ToolArguments = displayArguments,
                            ToolCallId = toolCallId,
                            ParentToolCallId = parentToolCallId
                        });
                });
                hadSnapshot = argumentContext.TryGetValue(CodingTools.BashSnapshotContextKey, out previousSnapshot);
                argumentContext[CodingTools.BashSnapshotContextKey] = (Action<BashToolUpdate>)(update =>
                {
                    if (toolExecutionContext is not null) toolExecutionContext.ReportBashProgress(update);
                    else publish(new AgentLifecycleEvent("tool_execution_update", Text: update.Text, Tool: Name,
                        OperationId: id, Details: update.Details)
                    {
                        ToolArguments = displayArguments,
                        ToolCallId = toolCallId,
                        ParentToolCallId = parentToolCallId,
                        OutputIsSnapshot = true,
                        OutputDelta = update.Delta
                    });
                });
            }
            if (loadout is not null)
            {
                hadPiSharpContext = argumentContext.TryGetValue(PiSharpToolExecutionContext.ContextKey, out previousPiSharpContext);
                argumentContext[PiSharpToolExecutionContext.ContextKey] = toolExecutionContext!;
            }
        }
        try
        {
            if (!isCallable)
                throw new InvalidOperationException($"Tool '{Name}' is not active or callable in this session.");
            CodingToolDeclarations.PrepareArguments(InnerFunction, invocationArguments);
            await (toolHooks?.BeforeAsync(new PiSharpToolCallContext(Name, toolCallId, parentToolCallId,
                invocationArguments, reachesTools, toolNamespace), cancellationToken) ?? ValueTask.CompletedTask);
            displayArguments = invocationArguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            PublishToolStarted();
            startedAt = Stopwatch.GetTimestamp();
            value = await base.InvokeCoreAsync(invocationArguments, cancellationToken);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (startedAt is { } timestamp)
                durationMs = Math.Floor(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds + 0.5);
            if (argumentContext is not null)
            {
                if (Name == "bash")
                {
                    if (hadUpdate) argumentContext[CodingTools.BashOutputContextKey] = previousUpdate!;
                    else argumentContext.Remove(CodingTools.BashOutputContextKey);
                    if (hadSnapshot) argumentContext[CodingTools.BashSnapshotContextKey] = previousSnapshot!;
                    else argumentContext.Remove(CodingTools.BashSnapshotContextKey);
                }
                if (loadout is not null)
                {
                    if (hadPiSharpContext) argumentContext[PiSharpToolExecutionContext.ContextKey] = previousPiSharpContext!;
                    else argumentContext.Remove(PiSharpToolExecutionContext.ContextKey);
                }
            }
        }
        // The tool's work is done. Hold the completion gate until this call has published its result so an
        // accepted steer cannot be observed before a tool that already finished (pinned Pi orders these on
        // one event loop; see ToolCompletionGate).
        using var completionScope = completionGate?.Begin();
        var resultArguments = invocationArguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var resultContext = new PiSharpToolResultContext(Name, toolCallId, parentToolCallId,
            resultArguments, value, failure);
        var builtinCancellationRequested = failure is OperationCanceledException &&
            cancellationToken.IsCancellationRequested && CodingToolDeclarations.IsBuiltin(InnerFunction);
        if (builtinCancellationRequested)
        {
            var message = string.IsNullOrWhiteSpace(failure!.Message) ? "Command aborted" : failure.Message;
            resultContext.Result = new PiSharpToolResult(message, Details: new Dictionary<string, object?>(),
                IsError: true, Error: message);
            resultContext.IsError = true;
            resultContext.Error = message;
        }
        if (ToolResultOutput.TryReadContract(value, out var returned) && returned.IsError)
        {
            resultContext.IsError = true;
            resultContext.Error = returned.Error ?? returned.Text;
        }
        var originalResult = resultContext.Result;
        ToolResultOutput.TryReadContract(originalResult, out var originalContract);
        try
        {
            if (toolHooks is not null)
                await toolHooks.AfterAsync(resultContext, cancellationToken);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            resultContext.Exception = error;
            resultContext.IsError = true;
            resultContext.Error ??= error.Message;
            failure = error;
        }
        catch (Exception error)
        {
            // Pinned Pi turns a failed result hook into an ordinary error result and keeps the
            // completed tool round in the conversation.
            resultContext.Exception = null;
            resultContext.Result = new PiSharpToolResult(error.Message, Details: new Dictionary<string, object?>(),
                IsError: true, Error: error.Message);
            resultContext.IsError = true;
            resultContext.Error = error.Message;
            failure = null;
        }
        if (resultContext.ResultChanged && !ReferenceEquals(resultContext.Result, originalResult) &&
            originalContract is not null)
        {
            var readable = ToolResultOutput.TryRead(resultContext.Result, out var replacementText,
                out var replacementDetails);
            if (!readable) replacementText = resultContext.Result?.ToString() ?? string.Empty;
            // Pi keeps the original details and usage when a hook replaces only content, but an
            // explicitly supplied replacement wins. Replacing content also drops structured data
            // and images that no longer describe it.
            if (ToolResultOutput.TryReadContract(resultContext.Result, out var replacementContract))
            {
                var contentReplaced = !string.Equals(replacementContract.Text, originalContract.Text,
                    StringComparison.Ordinal);
                resultContext.Result = replacementContract with
                {
                    Details = replacementContract.Details ?? originalContract.Details,
                    StructuredContent = contentReplaced ? replacementContract.StructuredContent :
                        replacementContract.StructuredContent ?? originalContract.StructuredContent,
                    Images = contentReplaced ? replacementContract.Images :
                        replacementContract.Images ?? originalContract.Images,
                    Usage = replacementContract.Usage ?? originalContract.Usage,
                    IsError = resultContext.IsError,
                    Error = resultContext.IsError ? resultContext.Error : null
                };
            }
            else
            {
                resultContext.Result = originalContract with
                {
                    Text = replacementText,
                    Details = replacementDetails ?? originalContract.Details,
                    StructuredContent = null,
                    Images = null,
                    IsError = resultContext.IsError,
                    Error = resultContext.IsError ? resultContext.Error : null
                };
            }
        }
        value = resultContext.Result;
        failure = resultContext.Exception;
        ToolResultOutput.TryReadContract(value, out var contract);
        if (failure is null && contract is not null && contract.IsError != resultContext.IsError)
        {
            contract = contract with
            {
                IsError = resultContext.IsError,
                Error = resultContext.IsError ? resultContext.Error : null
            };
            value = contract;
        }
        else if (failure is null && resultContext.IsError && contract is null)
        {
            ToolResultOutput.TryRead(value, out var returnedText, out var returnedDetails);
            contract = new PiSharpToolResult(returnedText.Length > 0 ? returnedText : value?.ToString() ?? string.Empty,
                returnedDetails, IsError: true, Error: resultContext.Error);
            value = contract;
        }
        if (nestedInvocation is not null && contract?.Usage is { } nestedUsage)
            nestedInvocation.Parent.RecordToolUsage(toolCallId, Name, nestedUsage, contract.Cost);
        if (failure is null && !resultContext.IsError)
        {
            try { _outputValidator.Validate(value); }
            catch (Exception error)
            {
                // PiSharp validates declared output schemas on top of Pi's declarative contract;
                // a violation is a visible returned error, never a successful tool result.
                failure = null;
                value = null;
                contract = null;
                resultContext.Exception = null;
                resultContext.Result = new PiSharpToolResult(error.Message, Details: new Dictionary<string, object?>(),
                    IsError: true, Error: error.Message);
                resultContext.IsError = true;
                resultContext.Error = error.Message;
                ToolResultOutput.TryReadContract(resultContext.Result, out contract);
            }
        }
        if (failure is not null)
        {
            resultContext.IsError = true;
            resultContext.Error ??= failure.Message;
        }
        if (builtinCancellationRequested && resultContext.Result is null)
        {
            var message = string.IsNullOrWhiteSpace(resultContext.Error) ? "Command aborted" : resultContext.Error;
            resultContext.Result = new PiSharpToolResult(message, Details: new Dictionary<string, object?>(),
                IsError: true, Error: message);
            resultContext.IsError = true;
            resultContext.Error = message;
        }
        value = resultContext.Result;
        var builtinCancelled = failure is OperationCanceledException && cancellationToken.IsCancellationRequested &&
            CodingToolDeclarations.IsBuiltin(InnerFunction);
        var builtinFailure = failure is ToolFailureException &&
            !cancellationToken.IsCancellationRequested && CodingToolDeclarations.IsBuiltin(InnerFunction);
        var cancelledNonBuiltin = failure is OperationCanceledException &&
            cancellationToken.IsCancellationRequested && !CodingToolDeclarations.IsBuiltin(InnerFunction);
        if (cancelledNonBuiltin && value is null)
        {
            // A cancelled external tool still reports an error result (Pi: "MCP request aborted" for
            // MCP), so the tool round settles with a result instead of dropping the whole batch.
            var cancelledText = string.IsNullOrWhiteSpace(failure!.Message) ? "Request was aborted" : failure.Message;
            value = new PiSharpToolResult(cancelledText, Details: new Dictionary<string, object?>(),
                IsError: true, Error: cancelledText);
        }
        if (builtinFailure)
        {
            // Keep the failed durable outcome while returning Pi's recoverable error text to MAF.
            contract = new PiSharpToolResult(failure!.Message, Details: new Dictionary<string, object?>(),
                IsError: true, Error: failure.Message);
            value = contract;
        }
        else if (failure is not null && !ToolResultOutput.TryReadContract(value, out _))
        {
            var message = string.IsNullOrWhiteSpace(failure.Message) ? "Tool execution failed." : failure.Message;
            contract = new PiSharpToolResult(message, Details: new Dictionary<string, object?>(),
                IsError: true, Error: message);
            value = contract;
            resultContext.Result = contract;
        }
        if (nestedInvocation is not null)
        {
            nestedInvocation.IsError = resultContext.IsError;
            nestedInvocation.Error = resultContext.Error;
        }
        IReadOnlyList<Microsoft.Extensions.AI.DataContent>? toolImages =
            ReadToolOutput.TryRead(value, out var readOutput) && readOutput.TryCreateImageContent(out var image) ? [image] : null;
        if (contract?.Images is { Count: > 0 })
            toolImages = contract.Images.Select(item => item.ToDataContent()).OfType<Microsoft.Extensions.AI.DataContent>().ToArray();
        ChatMessage? toolResultMessage = null;
        OperationCanceledException? messageLifecycleCancellation = null;
        if (callContent is not null)
        {
            var resultContent = new FunctionResultContent(callContent.CallId, value) { Exception = failure };
            ToolExecutionDuration.Set(resultContent, durationMs);
            toolResultMessage = new ChatMessage(ChatRole.Tool, [resultContent]);
            var toolMessageSnapshot = PiSharpExtensionMessageMapper.Snapshot(toolResultMessage);
            try
            {
                await (messageEvents?.DispatchMessageStartAsync(new(toolMessageSnapshot), cancellationToken) ?? Task.CompletedTask)
                    .ConfigureAwait(false);
                if (messageEvents is not null)
                {
                    var finalized = await messageEvents.DispatchMessageEndAsync(toolMessageSnapshot,
                        cancellationToken, replacement =>
                        {
                            var functionResults = replacement.Contents.Where(content =>
                                content.Kind == PiSharp.Extensions.Abstractions.PiSharpExtensionMessageContentKind.FunctionResult)
                                .ToArray();
                            return functionResults.Length == 1 && functionResults[0].CallId == callContent.CallId
                                ? null
                                : $"A tool-result message_end replacement must contain exactly one function result for call '{callContent.CallId}'.";
                        }).ConfigureAwait(false);
                    if (!ReferenceEquals(toolMessageSnapshot, finalized))
                    {
                        toolResultMessage = PiSharpExtensionMessageMapper.Restore(finalized);
                        var acceptedResult = toolResultMessage.Contents.OfType<FunctionResultContent>()
                            .Single(content => content.CallId == callContent.CallId);
                        value = acceptedResult.Result;
                        ToolResultOutput.TryReadContract(value, out var replacementContract);
                        contract = replacementContract;
                        resultContext.IsError = contract?.IsError == true;
                        resultContext.Error = contract?.Error;
                        var replacementImageContents = toolResultMessage.Contents
                            .OfType<Microsoft.Extensions.AI.DataContent>().ToArray();
                        if (replacementImageContents.Length > 0)
                        {
                            var replacementImages = replacementImageContents
                                .Select(content => new PiSharpToolImage(content.MediaType ?? "application/octet-stream",
                                    Convert.ToBase64String(content.Data.Span))).ToArray();
                            contract = contract is null
                                ? new PiSharpToolResult(value?.ToString() ?? string.Empty, Details: value,
                                    IsError: resultContext.IsError, Error: resultContext.Error, Images: replacementImages)
                                : contract with { Images = replacementImages };
                            value = contract;
                            var replacementContents = toolResultMessage.Contents
                                .Where(content => content is not Microsoft.Extensions.AI.DataContent).ToArray();
                            var functionResultIndex = Array.FindIndex(replacementContents,
                                content => content is FunctionResultContent result && result.CallId == callContent.CallId);
                            if (functionResultIndex >= 0)
                            {
                                replacementContents[functionResultIndex] = new FunctionResultContent(callContent.CallId, value)
                                {
                                    Exception = acceptedResult?.Exception,
                                    AdditionalProperties = acceptedResult?.AdditionalProperties?.Clone()
                                };
                            }
                            toolResultMessage = new ChatMessage(ChatRole.Tool, replacementContents);
                        }
                        toolImages = replacementImageContents.Length > 0 ? replacementImageContents :
                            contract?.Images?.Select(image => image.ToDataContent()).OfType<Microsoft.Extensions.AI.DataContent>().ToArray();
                    }
                }
            }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
            {
                messageLifecycleCancellation = error;
            }
        }
        var hasStructuredOutput = ToolResultOutput.TryRead(value, out var resultText, out var details);
        if (!hasStructuredOutput) resultText = value?.ToString();
        try
        {
            if (execution is not null) await execution.EndToolAsync(id, resultText, failure,
                resultContext.IsError && failure is null ? resultContext.Error ?? "Tool returned an error result." : null);
        }
        catch (Exception error)
        {
            publish(new("tool_outcome_unknown", Tool: Name, OperationId: id, IsError: true, Error: error.Message));
            throw;
        }
        if (invocationContext is not null &&
            termination.Record(invocationContext, failure is null && contract?.Terminate == true))
            invocationContext.Terminate = true;
        publish(new("tool_execution_finished", Text: resultText, Tool: Name, OperationId: id,
                IsError: resultContext.IsError, Error: resultContext.Error,
                Details: hasStructuredOutput ? details : null, Cost: contract?.Cost)
        {
            DurationMs = durationMs,
            Images = toolImages,
            StructuredContent = contract?.StructuredContent,
            ToolUsage = contract?.Usage,
            ToolResultIsError = contract?.IsError == true && !builtinCancelled,
            Terminate = contract?.Terminate,
            ToolArguments = displayArguments,
            ToolCallId = toolCallId,
            ParentToolCallId = parentToolCallId,
            NestedToolCalls = toolExecutionContext?.NestedCalls,
            ToolResultMessage = toolResultMessage
        });
        if (failure is not null && !builtinFailure && !builtinCancelled && !cancelledNonBuiltin)
            ExceptionDispatchInfo.Capture(failure).Throw();
        if (messageLifecycleCancellation is not null && !builtinCancelled && !cancelledNonBuiltin)
            ExceptionDispatchInfo.Capture(messageLifecycleCancellation).Throw();
        return value;
    }
}
