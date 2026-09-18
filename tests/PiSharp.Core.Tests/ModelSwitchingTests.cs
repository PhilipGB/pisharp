using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core;
using PiSharp.Core.Models;
using PiSharp.Core.Models.Providers;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// Live model/thinking switching on the session controller (pinned AgentSession.setModel /
/// setThinkingLevel / cycleModel + sessionManager.appendModelChange / appendThinkingLevelChange):
/// the in-memory state and the durable session entries must stay in lockstep, auth failures
/// must throw the pinned message without appending anything, and ephemeral sessions must
/// switch state without touching any document.
/// </summary>
public sealed class ModelSwitchingTests
{
    [Fact]
    public async Task SetModelSwitchesStateAndAppendsModelChangeOnly()
    {
        var temp = TempDirectory.Create();
        try
        {
            var controller = await CreateControllerAsync(temp, secondModel: true);
            var before = controller.Document!.Entries.Count;

            var target = controller.ModelState.Model!
                .Provider == ModelRuntimeTestKit.ProviderId
                ? controller.ModelRuntime.GetModels(ModelRuntimeTestKit.ProviderId)
                    .First(model => model.Id == ModelRuntimeTestKit.SecondModelId)
                : throw new InvalidOperationException("unexpected provider");
            var result = await controller.SetModelAsync(target, new ModelMutationOptions(), CancellationToken.None);

            Assert.True(result.ModelChanged);
            Assert.False(result.ThinkingChanged); // both kit models are non-reasoning: level stays clamped to off
            Assert.Equal(ModelRuntimeTestKit.SecondModelId, controller.ModelState.Model!.Id);

            var appended = controller.Document.Entries.Skip(before).ToList();
            var changes = appended.OfType<ModelChangeEntry>().ToList();
            Assert.Single(changes);
            Assert.Equal(ModelRuntimeTestKit.ProviderId, changes[0].Provider);
            Assert.Equal(ModelRuntimeTestKit.SecondModelId, changes[0].ModelId);
            Assert.Empty(appended.OfType<ThinkingLevelChangeEntry>());
        }
        finally
        {
            temp.Dispose();
        }
    }

    [Fact]
    public async Task SetModelWithoutAuthThrowsPinnedMessageAndAppendsNothing()
    {
        var temp = TempDirectory.Create();
        try
        {
            var controller = await CreateControllerAsync(temp);
            var before = controller.Document!.Entries.Count;
            var ghost = new ModelInfo
            {
                Id = "x",
                Name = "Ghost",
                Api = "openai-completions",
                Provider = "ghost",
                BaseUrl = "http://localhost:9/v1",
                Input = ["text"],
                Cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 },
                ContextWindow = 4_096,
                MaxTokens = 512,
            };

            var beforeModel = controller.ModelState.Model;
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                controller.SetModelAsync(ghost, new ModelMutationOptions(), CancellationToken.None));

            Assert.Equal("No API key for ghost/x", exception.Message);
            Assert.Same(beforeModel, controller.ModelState.Model);
            Assert.Equal(before, controller.Document.Entries.Count);
        }
        finally
        {
            temp.Dispose();
        }
    }

    [Fact]
    public async Task SetModelToReasoningModelAppendsModelAndThinkingEntries()
    {
        var temp = TempDirectory.Create();
        try
        {
            var controller = await CreateControllerAsync(temp, reasoningModel: true);
            // With no configured default the switch keeps the current level (pinned
            // fallback); configure one so the reasoning model receives a different level.
            controller.Settings.SetDefaultThinkingLevel("high");
            var before = controller.Document!.Entries.Count;
            var previousThinking = controller.ModelState.ThinkingLevel;

            var thinker = controller.ModelRuntime.GetModels("thinker").Single();
            await controller.SetModelAsync(thinker, new ModelMutationOptions(), CancellationToken.None);

            var appended = controller.Document.Entries.Skip(before).ToList();
            var changes = appended.OfType<ModelChangeEntry>().ToList();
            var thinking = appended.OfType<ThinkingLevelChangeEntry>().ToList();
            Assert.Single(changes);
            Assert.Equal("thinker", changes[0].Provider);
            Assert.Equal("mini", changes[0].ModelId);

            // The applied level (global default clamped to the model) differs from the
            // previous off level, so exactly one thinking entry follows the model entry.
            Assert.Single(thinking);
            Assert.Equal(changes[0].Id, thinking[0].ParentId);
            Assert.Equal("high", thinking[0].ThinkingLevel);
            Assert.NotEqual(previousThinking, thinking[0].ThinkingLevel);
            Assert.Equal("high", controller.ModelState.ThinkingLevel);
        }
        finally
        {
            temp.Dispose();
        }
    }

    [Fact]
    public async Task SetThinkingLevelAppendsOnlyWhenTheLevelChanges()
    {
        var temp = TempDirectory.Create();
        try
        {
            var controller = await CreateControllerAsync(temp, reasoningModel: true);
            var thinker = controller.ModelRuntime.GetModels("thinker").Single();
            await controller.SetModelAsync(thinker, new ModelMutationOptions(), CancellationToken.None);
            var before = controller.Document!.Entries.Count;

            // Re-setting the current level is a no-op in pinned: no entry.
            var same = await controller.SetThinkingLevelAsync(
                controller.ModelState.ThinkingLevel!, new ModelMutationOptions(), CancellationToken.None);
            Assert.False(same.Changed);
            Assert.Equal(before, controller.Document.Entries.Count);

            var other = GetOtherLevel(controller.ModelState);
            var result = await controller.SetThinkingLevelAsync(other, new ModelMutationOptions(), CancellationToken.None);
            Assert.True(result.Changed);
            Assert.Equal(other, controller.ModelState.ThinkingLevel);

            var appended = controller.Document.Entries.Skip(before).ToList();
            var thinking = appended.OfType<ThinkingLevelChangeEntry>().ToList();
            Assert.Single(thinking);
            Assert.Equal(other, thinking[0].ThinkingLevel);
            Assert.Empty(appended.OfType<ModelChangeEntry>());
        }
        finally
        {
            temp.Dispose();
        }
    }

    [Fact]
    public async Task CycleModelStepsThroughAvailableModelsAndAppendsEntries()
    {
        var temp = TempDirectory.Create();
        try
        {
            var controller = await CreateControllerAsync(temp, secondModel: true);
            var before = controller.Document!.Entries.Count;

            var forward = await controller.CycleModelAsync(
                "forward", new ModelMutationOptions(), CancellationToken.None);
            Assert.NotNull(forward);
            Assert.False(forward.IsScoped);
            Assert.Equal(ModelRuntimeTestKit.SecondModelId, forward!.Model.Id);

            var back = await controller.CycleModelAsync(
                "backward", new ModelMutationOptions(), CancellationToken.None);
            Assert.NotNull(back);
            Assert.Equal(ModelRuntimeTestKit.ModelId, back!.Model.Id);
            Assert.Equal(ModelRuntimeTestKit.ModelId, controller.ModelState.Model!.Id);

            var changes = controller.Document.Entries.Skip(before).OfType<ModelChangeEntry>().ToList();
            Assert.Equal(2, changes.Count);
            Assert.Equal(ModelRuntimeTestKit.SecondModelId, changes[0].ModelId);
            Assert.Equal(ModelRuntimeTestKit.ModelId, changes[1].ModelId);
        }
        finally
        {
            temp.Dispose();
        }
    }

    [Fact]
    public async Task CycleModelIsANoopWithASingleCandidate()
    {
        var temp = TempDirectory.Create();
        try
        {
            var controller = await CreateControllerAsync(temp);
            var before = controller.Document!.Entries.Count;

            var result = await controller.CycleModelAsync("forward", new ModelMutationOptions(), CancellationToken.None);

            Assert.Null(result);
            Assert.Equal(before, controller.Document.Entries.Count);
        }
        finally
        {
            temp.Dispose();
        }
    }

    [Fact]
    public async Task CycleThinkingReturnsNullWhenTheModelDoesNotSupportThinking()
    {
        var temp = TempDirectory.Create();
        try
        {
            var controller = await CreateControllerAsync(temp);
            var before = controller.Document!.Entries.Count;

            var level = await controller.CycleThinkingLevelAsync(new ModelMutationOptions(), CancellationToken.None);

            Assert.Null(level);
            Assert.Equal(before, controller.Document.Entries.Count);
        }
        finally
        {
            temp.Dispose();
        }
    }

    [Fact]
    public async Task CycleThinkingStepsToTheNextLevelAndAppendsAnEntry()
    {
        var temp = TempDirectory.Create();
        try
        {
            var controller = await CreateControllerAsync(temp, reasoningModel: true);
            var thinker = controller.ModelRuntime.GetModels("thinker").Single();
            await controller.SetModelAsync(thinker, new ModelMutationOptions(), CancellationToken.None);
            var before = controller.Document!.Entries.Count;
            var previous = controller.ModelState.ThinkingLevel!;

            var next = await controller.CycleThinkingLevelAsync(new ModelMutationOptions(), CancellationToken.None);

            Assert.NotNull(next);
            Assert.NotEqual(previous, next);
            Assert.Equal(next, controller.ModelState.ThinkingLevel);

            var thinking = controller.Document.Entries.Skip(before).OfType<ThinkingLevelChangeEntry>().ToList();
            Assert.Single(thinking);
            Assert.Equal(next, thinking[0].ThinkingLevel);
        }
        finally
        {
            temp.Dispose();
        }
    }

    [Fact]
    public async Task SetModelWithPersistUpdatesTheGlobalDefaults()
    {
        var temp = TempDirectory.Create();
        try
        {
            var controller = await CreateControllerAsync(temp, secondModel: true);
            var target = controller.ModelRuntime.GetModels(ModelRuntimeTestKit.ProviderId)
                .First(model => model.Id == ModelRuntimeTestKit.SecondModelId);

            await controller.SetModelAsync(target, new ModelMutationOptions(Persist: true), CancellationToken.None);

            Assert.Equal(ModelRuntimeTestKit.ProviderId, controller.Settings.GetDefaultProvider());
            Assert.Equal(ModelRuntimeTestKit.SecondModelId, controller.Settings.GetDefaultModel());
        }
        finally
        {
            temp.Dispose();
        }
    }

    [Fact]
    public async Task EphemeralSessionSwitchesStateWithoutTouchingADocument()
    {
        var temp = TempDirectory.Create();
        try
        {
            var controller = await CreateControllerAsync(temp, secondModel: true, noSession: true);
            Assert.False(controller.IsPersistent);
            Assert.Null(controller.Document);

            var target = controller.ModelRuntime.GetModels(ModelRuntimeTestKit.ProviderId)
                .First(model => model.Id == ModelRuntimeTestKit.SecondModelId);
            var result = await controller.SetModelAsync(target, new ModelMutationOptions(), CancellationToken.None);

            Assert.True(result.ModelChanged);
            Assert.Equal(ModelRuntimeTestKit.SecondModelId, controller.ModelState.Model!.Id);
        }
        finally
        {
            temp.Dispose();
        }
    }

    /// <summary>Picks a selectable level different from the current one for the live model.</summary>
    private static string GetOtherLevel(ModelSessionState state)
    {
        var levels = state.GetAvailableThinkingLevels();
        return levels.First(level => !string.Equals(level, state.ThinkingLevel, StringComparison.Ordinal));
    }

    /// <summary>Summary client that never runs: switching tests issue no model requests.</summary>
    private sealed class NoopChatClient : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("switching tests never issue model requests");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("switching tests never issue model requests");
        }
    }

    private static async Task<SessionController> CreateControllerAsync(
        TempDirectory temp,
        bool secondModel = false,
        bool reasoningModel = false,
        bool noSession = false)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var (runtime, state, settings) = await ModelRuntimeTestKit.CreateAsync(
            secondModel: secondModel,
            reasoningModel: reasoningModel);

        var bootstrap = new AgentBootstrap(
            Agent: new SessionOperationTests.RecordingAgent(),
            SummaryClient: new NoopChatClient(),
            ContextFiles: [],
            Skills: [],
            PromptTemplates: [],
            ExtensionHost: new PiSharpExtensionHost(),
            RetryPolicy: RetryPolicyOptions.Disabled,
            TurnQueue: new TurnMessageQueue(),
            SessionHistory: new PiSessionChatHistoryProvider(),
            Compaction: new CompactionTarget(),
            ModelRuntime: runtime,
            ModelState: state);

        var options = new CliOptions(
            WorkingDirectory: workspace,
            Model: ModelRuntimeTestKit.Reference,
            Endpoint: null,
            ApiKey: null,
            Provider: null,
            Models: [],
            Thinking: null,
            ListModels: false,
            Offline: true,
            ContextTokens: 128_000,
            MaxOutputTokens: 16_384,
            ContextTokensExplicit: false,
            MaxOutputTokensExplicit: false,
            Prompt: null,
            FilePaths: [],
            ShowHelp: false,
            ContinueSession: false,
            ResumeSession: false,
            SessionSelector: null,
            SessionName: null,
            SessionDirectory: Path.Combine(temp.Path, "sessions"),
            NoSession: noSession,
            ContextRoot: null,
            ExtensionPaths: [],
            SkillPaths: [],
            PromptTemplatePaths: [],
            NoExtensions: true,
            NoSkills: true,
            NoPromptTemplates: true,
            ProjectTrustOverride: true,
            OutputMode: OutputMode.Text,
            PrintMode: false,
            ReadOnly: false,
            NoTools: false,
            AutoRetry: false);

        // The session shares the kit's settings instance so --persist assertions hit the
        // same defaults the model state mutates (production wires one shared instance).
        return await SessionController.CreateAsync(bootstrap, options, CancellationToken.None, settings);
    }
}
