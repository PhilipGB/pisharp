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

internal sealed record InitialModelSelection(ModelSelection Selection, string Thinking);

internal static class InitialModelComposition
{
    public static async Task<InitialModelSelection?> ResolveAsync(ProviderModelRuntime modelRuntime, CliArguments cli,
        string? explicitThinking, UserSettings userSettings)
    {
        ModelSelection selection;
        var thinking = cli.Thinking ?? "off";
        try
        {
            selection = await modelRuntime.ResolveAsync(cli.Provider ?? (cli.Local ? "local" : null), cli.ModelOverride);
            thinking = explicitThinking ?? userSettings.GetModelThinkingLevel(selection) ?? cli.Thinking ?? ThinkingLevels.Default;
            thinking = ThinkingLevels.ValidateForModel(thinking, selection.Model.Reasoning, selection.Model.ThinkingLevelMap);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine(error.Message);
            Environment.ExitCode = 2;
            return null;
        }
        return new(selection, thinking);
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
