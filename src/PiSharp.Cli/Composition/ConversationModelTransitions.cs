using PiSharp.Cli.Sessions;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli.Composition;

internal sealed class ConversationModelTransitions(ActiveProjectRuntime projectBinding, ActiveModelRuntime modelBinding,
    ActiveConversationRuntime conversationBinding, ModelRuntimeController modelRuntimeController,
    ICodemodeModels codemodeModels, ConversationRunComposition runComposition)
{
    public async Task AdoptPreparedAsync(PreparedModelRuntime prepared, bool recordModelChange)
    {
        var nextAgent = projectBinding.Runtime.CreateAgent(prepared.ChatClient, prepared.Selection, prepared.Thinking, projectBinding.Arguments, projectBinding.Settings,
            prepared.VirtualModelRouter, codemodeModels);
        var nextRun = await ConversationModelReplacement.ReplaceAsync(conversationBinding.Conversation, modelBinding.Selection, modelBinding.Thinking, prepared,
            recordModelChange,
            () => runComposition.OpenAsync(modelBinding, nextAgent, conversationBinding.Conversation, conversationBinding.Path, prepared.Selection.Provider.Id, prepared.Thinking,
                targetRuntime: prepared),
            conversationBinding.Path is null ? null : () => projectBinding.Store.SaveAsync(conversationBinding.Conversation, conversationBinding.Path));
        modelBinding.AdoptPrepared(prepared, nextAgent);
        conversationBinding.ReplaceRun(nextRun);
    }

    public Task ReplaceAsync(ModelSelection nextSelection, string nextThinking, bool recordModelChange,
        bool requireAuthenticated = true) => AdoptPreparedAsync(
            modelRuntimeController.Prepare(nextSelection, nextThinking, requireAuthenticated), recordModelChange);

    public Task ApplySelectionAsync(ModelSelection nextSelection, string nextThinking) =>
        ApplySelectionAsync(nextSelection, nextThinking, PiSharpExtensionModelSelectionSource.Set,
            CancellationToken.None);

    public async Task ApplySelectionAsync(ModelSelection nextSelection, string nextThinking,
        PiSharpExtensionModelSelectionSource source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var previousSelection = modelBinding.Selection;
        var previousThinking = modelBinding.Thinking;
        await modelRuntimeController.ApplySelectionAsync(nextSelection, nextThinking, modelBinding.Agent, conversationBinding.Run,
            prepared =>
            {
                modelBinding.AdoptPrepared(prepared);
            }, prepared => AdoptPreparedAsync(prepared, recordModelChange: true));
        await PiSharpExtensionSelectionEvents.PublishChangesAsync(modelBinding.Agent, previousSelection,
            previousThinking, modelBinding.Selection, modelBinding.Thinking, source, cancellationToken).ConfigureAwait(false);
    }

    public Task<ModelDescriptor> SelectRpcModelAsync(ModelDescriptor model, CancellationToken token) =>
        SelectRpcModelAsync(model, PiSharpExtensionModelSelectionSource.Set, token);

    public async Task<ModelDescriptor> SelectRpcModelAsync(ModelDescriptor model,
        PiSharpExtensionModelSelectionSource source, CancellationToken token)
    {
        var nextSelection = await modelRuntimeController.ResolveRpcModelAsync(model, token);
        var nextThinking = modelRuntimeController.ResolveThinkingLevelForModelSwitch(nextSelection, modelBinding.Thinking);
        await ApplySelectionAsync(nextSelection, nextThinking, source, token);
        return modelBinding.Selection.Model;
    }

    public async Task<string> SetRpcThinkingLevelAsync(string level, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var normalized = ThinkingLevels.Normalize(level);
        var nextThinking = ThinkingLevels.ValidateForModel(normalized, modelBinding.Selection.Model.Reasoning,
            modelBinding.Selection.Model.ThinkingLevelMap);
        if (nextThinking.Equals(modelBinding.Thinking, StringComparison.Ordinal)) return modelBinding.Thinking;
        var previousThinking = modelBinding.Thinking;
        await ReplaceAsync(modelBinding.Selection, nextThinking, recordModelChange: false);
        await PiSharpExtensionSelectionEvents.PublishChangesAsync(modelBinding.Agent, modelBinding.Selection,
            previousThinking, modelBinding.Selection, modelBinding.Thinking,
            PiSharpExtensionModelSelectionSource.Set, token).ConfigureAwait(false);
        return modelBinding.Thinking;
    }

    public async Task<string> SetRpcThinkingLevelDuringRunAsync(string level, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var normalized = ThinkingLevels.Normalize(level);
        var nextThinking = ThinkingLevels.ValidateForModel(normalized, modelBinding.Selection.Model.Reasoning,
            modelBinding.Selection.Model.ThinkingLevelMap);
        if (nextThinking.Equals(modelBinding.Thinking, StringComparison.Ordinal)) return modelBinding.Thinking;
        if (!conversationBinding.Run.SetThinkingLevelDuringRun(nextThinking,
            ThinkingLevels.ToOptions(nextThinking, modelBinding.Selection.Model.ThinkingLevelMap)))
            throw new InvalidOperationException("The active run is already settling.");
        var previousThinking = modelBinding.Thinking;
        modelBinding.SetThinking(nextThinking);
        await PiSharpExtensionSelectionEvents.PublishChangesAsync(modelBinding.Agent, modelBinding.Selection,
            previousThinking, modelBinding.Selection, modelBinding.Thinking,
            PiSharpExtensionModelSelectionSource.Set, token).ConfigureAwait(false);
        return modelBinding.Thinking;
    }

    public async Task<string> SelectThinkingLevelAsync(string level, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ThinkingLevels.Normalize(level);
        var nextThinking = ThinkingLevels.ValidateForModel(normalized, modelBinding.Selection.Model.Reasoning,
            modelBinding.Selection.Model.ThinkingLevelMap);
        if (nextThinking.Equals(modelBinding.Thinking, StringComparison.Ordinal)) return modelBinding.Thinking;
        var previousThinking = modelBinding.Thinking;
        await ReplaceAsync(modelBinding.Selection, nextThinking, recordModelChange: false);
        await PiSharpExtensionSelectionEvents.PublishChangesAsync(modelBinding.Agent, modelBinding.Selection,
            previousThinking, modelBinding.Selection, modelBinding.Thinking,
            PiSharpExtensionModelSelectionSource.Set, cancellationToken).ConfigureAwait(false);
        return modelBinding.Thinking;
    }

}
