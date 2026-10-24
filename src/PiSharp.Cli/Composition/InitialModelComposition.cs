using Microsoft.Extensions.AI;
using PiSharp.Cli.Sessions;
using PiSharp.Runtime;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Composition;

internal sealed record InitialModelSelection(ModelSelection Selection, string Thinking);

internal static class InitialModelComposition
{
    public static async Task<InitialModelSelection?> ResolveAsync(ProviderModelRuntime modelRuntime, CliArguments cli,
        string? explicitThinking, UserSettings userSettings)
    {
        ModelSelection? selection = null;
        var usedInitialFallback = false;
        var continuing = cli.Continue || cli.SessionPath is not null || cli.ForkSource is not null;
        var thinking = cli.Thinking ?? "off";
        try
        {
            if (!cli.HasExplicitModelSelection)
            {
                if (modelRuntime.HasScope && !continuing)
                {
                    var scope = await modelRuntime.ResolveScopeAsync();
                    foreach (var diagnostic in scope.Diagnostics)
                        Console.Error.WriteLine($"Warning: {diagnostic.Message}");
                    if (scope.Models.FirstOrDefault() is { } first && first.Model.Provider is { } provider)
                    {
                        selection = await modelRuntime.ResolveAsync(provider, first.Model.Id, includeOutOfScope: true);
                        selection = selection with
                        {
                            RequestedThinkingLevel = first.ThinkingLevel ?? selection.RequestedThinkingLevel
                        };
                    }
                }

                if (selection is null)
                {
                    var initial = await ResolveDefaultOrAvailableAsync(modelRuntime, userSettings);
                    selection = initial.Selection;
                    usedInitialFallback = initial.UsedFallback;
                }
                if (selection is null && (continuing || cli.Mode == "rpc"))
                {
                    // Keep a usable runtime identity for session browsing and model setup when no
                    // provider is authenticated yet. Requests remain auth-gated by the provider.
                    var provider = userSettings.DefaultProvider ?? "openai";
                    var modelId = userSettings.DefaultModel ?? PinnedProviderModelDefaults.GetModel(provider) ??
                        PinnedProviderModelDefaults.GetModel("openai")!;
                    selection = await modelRuntime.ResolveAsync(provider, modelId, includeOutOfScope: true);
                    usedInitialFallback = true;
                }
            }
            else
                selection = await modelRuntime.ResolveAsync(
                    cli.Provider ?? (cli.Local ? "local" : null), cli.ModelOverride,
                    includeOutOfScope: continuing || cli.HasExplicitModelSelection,
                    allowThinkingSuffix: explicitThinking is null, customThinkingLevel: explicitThinking);
            if (selection is null) return null;
            thinking = explicitThinking ?? (usedInitialFallback ? ThinkingLevels.Default :
                selection.RequestedThinkingLevel ?? userSettings.GetModelThinkingLevel(selection) ??
                cli.Thinking ?? ThinkingLevels.Default);
            thinking = ThinkingLevels.ValidateForModel(thinking, selection.Model.Reasoning, selection.Model.ThinkingLevelMap);
            _ = selection.Connection;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine(error.Message);
            Environment.ExitCode = 2;
            return null;
        }
        if (selection.ResolutionWarning is { } warning)
            Console.Error.WriteLine($"Warning: {warning}");
        return new(selection, thinking);
    }

    private static async Task<(ModelSelection? Selection, bool UsedFallback)> ResolveDefaultOrAvailableAsync(
        ProviderModelRuntime modelRuntime,
        UserSettings userSettings)
    {
        if (userSettings.DefaultProvider is { Length: > 0 } provider &&
            userSettings.DefaultModel is { Length: > 0 } modelId)
        {
            try
            {
                var profile = modelRuntime.GetProvider(provider);
                var knownModels = profile.Models.Concat(modelRuntime.GetAvailableModelSnapshot());
                if (knownModels.Any(model => model.Provider?.Equals(provider, StringComparison.OrdinalIgnoreCase) == true &&
                    model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)))
                {
                    var saved = await modelRuntime.ResolveAsync(provider, modelId, includeOutOfScope: true);
                    if (saved.Authenticated) return (saved, false);
                }
            }
            catch (ArgumentException)
            {
                // A stale default falls through to Pi's first-authenticated-model recovery.
            }
        }

        var available = modelRuntime.GetAvailableModelSnapshot();
        foreach (var (preferredProvider, preferredModel) in PinnedProviderModelDefaults.Models)
        {
            if (available.FirstOrDefault(model => model.Provider?.Equals(preferredProvider,
                    StringComparison.OrdinalIgnoreCase) == true && model.Id.Equals(preferredModel,
                    StringComparison.Ordinal)) is { } preferred)
                return (await modelRuntime.ResolveAsync(preferredProvider, preferred.Id, includeOutOfScope: true), true);
        }

        if (available.FirstOrDefault() is { Provider: { } firstProvider } first)
            return (await modelRuntime.ResolveAsync(firstProvider, first.Id, includeOutOfScope: true), true);
        return (null, true);
    }
    public static ActiveModelRuntime? Create(ProjectRuntimeContext projectRuntime, IChatClient chat,
        ModelSelection selection, string thinking, CliArguments cli, UserSettings userSettings,
        ModelRuntimeController modelRuntimeController, ICodemodeModels codemodeModels)
    {
        PiAgent agent;
        try
        {
            agent = projectRuntime.CreateAgent(chat, selection, thinking, cli, userSettings,
                modelRuntimeController.CreateVirtualModelRouter(selection), codemodeModels);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Environment.ExitCode = 2;
            return null;
        }
        AutoCompactionPolicy? contextPolicy;
        ModelPricing? modelPricing;
        try
        {
            contextPolicy = userSettings.ResolveCompactionPolicy(selection.Model.ContextLength, Environment.GetEnvironmentVariable, $"{selection.Provider.Id}/{selection.Model.Id}");
            modelPricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? selection.Model.Pricing;
        }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 2; return null; }
        return new(selection, chat, agent, thinking, contextPolicy, modelPricing);
    }
}
