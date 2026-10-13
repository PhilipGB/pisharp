using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Cli.Tui;
using PiSharp.Cli.Protocols;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Startup;
using PiSharp.Cli.Composition;

using PiSharp.Runtime.Codemode;

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

    public async Task ApplySelectionAsync(ModelSelection nextSelection, string nextThinking)
    {
        await modelRuntimeController.ApplySelectionAsync(nextSelection, nextThinking, modelBinding.Agent, conversationBinding.Run,
            prepared =>
            {
                modelBinding.AdoptPrepared(prepared);
            }, prepared => AdoptPreparedAsync(prepared, recordModelChange: true));
    }

    public async Task<ModelDescriptor> SelectRpcModelAsync(ModelDescriptor model, CancellationToken token)
    {
        var nextSelection = await modelRuntimeController.ResolveRpcModelAsync(model, token);
        var nextThinking = modelRuntimeController.ResolveThinkingLevelForModelSwitch(nextSelection, modelBinding.Thinking);
        await ApplySelectionAsync(nextSelection, nextThinking);
        return modelBinding.Selection.Model;
    }

    public async Task<string> SetRpcThinkingLevelAsync(string level, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var normalized = ThinkingLevels.Normalize(level);
        var nextThinking = ThinkingLevels.ValidateForModel(normalized, modelBinding.Selection.Model.Reasoning,
            modelBinding.Selection.Model.ThinkingLevelMap);
        if (nextThinking.Equals(modelBinding.Thinking, StringComparison.Ordinal)) return modelBinding.Thinking;
        await ReplaceAsync(modelBinding.Selection, nextThinking, recordModelChange: false);
        return modelBinding.Thinking;
    }

    public Task<string> SetRpcThinkingLevelDuringRunAsync(string level, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var normalized = ThinkingLevels.Normalize(level);
        var nextThinking = ThinkingLevels.ValidateForModel(normalized, modelBinding.Selection.Model.Reasoning,
            modelBinding.Selection.Model.ThinkingLevelMap);
        if (nextThinking.Equals(modelBinding.Thinking, StringComparison.Ordinal)) return Task.FromResult(modelBinding.Thinking);
        if (!conversationBinding.Run.SetThinkingLevelDuringRun(nextThinking,
            ThinkingLevels.ToOptions(nextThinking, modelBinding.Selection.Model.ThinkingLevelMap)))
            throw new InvalidOperationException("The active run is already settling.");
        modelBinding.SetThinking(nextThinking);
        return Task.FromResult(modelBinding.Thinking);
    }

}
