using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core;
using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// Usage/cost persistence tests: raw openai-completions counts map to the pinned Usage shape
/// and the durable assistant entry carries provider/model/usage/cost (pinned AssistantMessage
/// fields api/provider/model/responseModel/usage).
/// </summary>
public sealed class UsageCostEntryTests
{
    [Theory]
    [InlineData(100L, 40L, 0L, 50L, 15L, 60L, 150L)]
    [InlineData(100L, 0L, 30L, 20L, null, 70L, 120L)]
    [InlineData(50L, 80L, 10L, 10L, null, 0L, 100L)] // clamped at zero
    [InlineData(0L, 0L, 0L, 0L, null, 0L, 0L)]
    public void FromOpenAiCountsSeparatesCacheFromPromptAndRecomputesTotal(
        long prompt,
        long cacheRead,
        long cacheWrite,
        long output,
        long? reasoning,
        long expectedInput,
        long expectedTotal)
    {
        var usage = ModelUsage.FromOpenAiCounts(prompt, cacheRead, cacheWrite, output, reasoning);

        Assert.Equal(expectedInput, usage.Input);
        Assert.Equal(output, usage.Output);
        Assert.Equal(cacheRead, usage.CacheRead);
        Assert.Equal(cacheWrite, usage.CacheWrite);
        Assert.Equal(reasoning, usage.Reasoning);
        Assert.Equal(expectedTotal, usage.TotalTokens);
    }

    [Fact]
    public async Task AssistantEntryPersistsProviderModelUsageAndCost()
    {
        using var temp = TempDirectory.Create();
        var harness = await BuildHarnessAsync(temp);

        await AgentTurnRunner.RunAsync(
            harness.Bootstrap,
            harness.Sessions,
            new LiveTurnCoordinator(harness.Bootstrap.TurnQueue),
            "say hi",
            harness.Workspace,
            new CompactionRuntimeTests.CapturingChatOutput(),
            CancellationToken.None);

        var entry = harness.Sessions.Document!.GetActiveEntryPath(harness.Sessions.ActiveEntryId)
            .OfType<MessageEntry>()
            .Single(entry => entry.Message.TryGetProperty("role", out var role) && role.GetString() == "assistant")
            .Message;

        // Pinned AssistantMessage identity fields: the requested model is authoritative.
        Assert.Equal("openai-completions", entry.GetProperty("api").GetString());
        Assert.Equal("local", entry.GetProperty("provider").GetString());
        Assert.Equal("test-model", entry.GetProperty("model").GetString());
        Assert.False(entry.TryGetProperty("responseModel", out _), "no responseModel without a provider-reported model");

        // Pinned Usage shape with cached tokens carved out of the prompt total.
        var usage = entry.GetProperty("usage");
        Assert.Equal(60, usage.GetProperty("input").GetInt64()); // 100 prompt - 40 cached
        Assert.Equal(50, usage.GetProperty("output").GetInt64());
        Assert.Equal(40, usage.GetProperty("cacheRead").GetInt64());
        Assert.Equal(0, usage.GetProperty("cacheWrite").GetInt64());
        Assert.Equal(15, usage.GetProperty("reasoning").GetInt64());
        Assert.False(usage.TryGetProperty("cacheWrite1h", out _));
        Assert.Equal(150, usage.GetProperty("totalTokens").GetInt64());

        // Cost from the model's catalogue rates: 2/1e6 * 60 + 8/1e6 * 50 + 0.5/1e6 * 40.
        var cost = usage.GetProperty("cost");
        Assert.Equal(0.00012, cost.GetProperty("input").GetDouble(), 10);
        Assert.Equal(0.0004, cost.GetProperty("output").GetDouble(), 10);
        Assert.Equal(0.00002, cost.GetProperty("cacheRead").GetDouble(), 10);
        Assert.Equal(0, cost.GetProperty("cacheWrite").GetDouble(), 10);
        Assert.Equal(0.00054, cost.GetProperty("total").GetDouble(), 10);
    }

    [Fact]
    public async Task AssistantEntryRecordsDifferingProviderReportedModelAsResponseModel()
    {
        using var temp = TempDirectory.Create();
        var harness = await BuildHarnessAsync(temp, reportedModel: "gateway/actual-model");

        await AgentTurnRunner.RunAsync(
            harness.Bootstrap,
            harness.Sessions,
            new LiveTurnCoordinator(harness.Bootstrap.TurnQueue),
            "say hi",
            harness.Workspace,
            new CompactionRuntimeTests.CapturingChatOutput(),
            CancellationToken.None);

        var entry = harness.Sessions.Document!.GetActiveEntryPath(harness.Sessions.ActiveEntryId)
            .OfType<MessageEntry>()
            .Single(entry => entry.Message.TryGetProperty("role", out var role) && role.GetString() == "assistant")
            .Message;

        Assert.Equal("test-model", entry.GetProperty("model").GetString());
        Assert.Equal("gateway/actual-model", entry.GetProperty("responseModel").GetString());
    }

    [Fact]
    public async Task AssistantEntryOmitsUsageWhenProviderReportsNone()
    {
        using var temp = TempDirectory.Create();
        var harness = await BuildHarnessAsync(temp, reportUsage: false);

        await AgentTurnRunner.RunAsync(
            harness.Bootstrap,
            harness.Sessions,
            new LiveTurnCoordinator(harness.Bootstrap.TurnQueue),
            "say hi",
            harness.Workspace,
            new CompactionRuntimeTests.CapturingChatOutput(),
            CancellationToken.None);

        var entry = harness.Sessions.Document!.GetActiveEntryPath(harness.Sessions.ActiveEntryId)
            .OfType<MessageEntry>()
            .Single(entry => entry.Message.TryGetProperty("role", out var role) && role.GetString() == "assistant")
            .Message;

        Assert.False(entry.TryGetProperty("usage", out _));
        // Identity fields are still recorded without usage.
        Assert.Equal("local", entry.GetProperty("provider").GetString());
        Assert.Equal("test-model", entry.GetProperty("model").GetString());
    }

    private sealed record TestHarness
    (
        string Workspace,
        AgentBootstrap Bootstrap,
        SessionController Sessions
    );

    private static async Task<TestHarness> BuildHarnessAsync(
        TempDirectory temp,
        string? reportedModel = null,
        bool reportUsage = true)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var turnQueue = new TurnMessageQueue();
        var sessionHistory = new PiSessionChatHistoryProvider();
        var compactionTarget = new CompactionTarget();

        var client = new UsageChatClient(reportedModel, reportUsage);
        var leaf = new CompactionChatClient(new SteeringChatClient(client, turnQueue, message => message),
            () => compactionTarget.Current);

#pragma warning disable MAAI001 // Harness token-limit options are evaluation-only; omitted here.
        var agent = leaf.AsHarnessAgent(new HarnessAgentOptions
        {
            ChatHistoryProvider = sessionHistory,
            Name = "pisharp-usage-test",
            HarnessInstructions = "Test harness.",
            ChatOptions = new ChatOptions { Instructions = "You are a test agent." },
            DisableCompaction = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableOpenTelemetry = true,
        });
#pragma warning restore MAAI001

        var settings = await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage());
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            Builtins = [],
            ModelsStore = new InMemoryModelsStore(),
            ModelsPath = null,
            NetworkEnabled = false,
            RefreshOnCreate = false,
        });

        // Non-zero catalogue rates so the persisted cost is observable.
        var model = new ModelInfo
        {
            Id = "test-model",
            Name = "Test Model",
            Api = "openai-completions",
            Provider = "local",
            BaseUrl = "http://localhost:9/v1",
            Input = ["text"],
            Cost = new ModelCost { Input = 2, Output = 8, CacheRead = 0.5, CacheWrite = 1 },
            ContextWindow = 128_000,
            MaxTokens = 16_384,
        };

        runtime.RegisterProvider(new ProviderSpec
        {
            Id = "local",
            Name = "Local",
            BaseUrl = "http://localhost:9/v1",
            Auth = new ProviderAuth(new EnvApiKeyAuth { Name = "k", EnvironmentVariableNames = ["LOCAL_TEST_KEY"] }),
            GetModels = () => new[] { model },
            DefaultApi = "openai-completions",
        });
        await runtime.SetRuntimeApiKeyAsync("local", "test-key");

        var state = new ModelSessionState(runtime, settings);
        var bootstrap = new AgentBootstrap(
            Agent: agent,
            SummaryClient: new ThrowingChatClient(),
            ContextFiles: [],
            Skills: [],
            PromptTemplates: [],
            ExtensionHost: new PiSharpExtensionHost(),
            RetryPolicy: RetryPolicyOptions.Disabled,
            TurnQueue: turnQueue,
            SessionHistory: sessionHistory,
            Compaction: compactionTarget,
            ModelRuntime: runtime,
            ModelState: state);

        var options = new CliOptions(
            WorkingDirectory: workspace,
            Model: "local/test-model",
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
            NoSession: false,
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

        var sessions = await SessionController.CreateAsync(bootstrap, options, CancellationToken.None);
        return new TestHarness(workspace, bootstrap, sessions);
    }

    /// <summary>
    /// One scripted streaming response: a text update plus (optionally) a UsageContent with the
    /// OpenAI adapter's usage details, mirroring what the real adapter emits on the final update.
    /// </summary>
    private sealed class UsageChatClient(string? reportedModel, bool reportUsage) : IChatClient
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
            throw new NotSupportedException("usage tests stream");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            StreamAsync(cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, new AIContent[] { new TextContent("done") });
            if (!reportUsage)
            {
                yield break;
            }

            var details = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 50,
                TotalTokenCount = 150,
                CachedInputTokenCount = 40,
                ReasoningTokenCount = 15,
            };
            // MAF copies ModelId/ResponseId from the ChatResponseUpdate onto the agent update
            // (mirroring the adapter stamping chunk.model), so the reported model rides here.
            var update = new ChatResponseUpdate(ChatRole.Assistant, new AIContent[] { new UsageContent(details) });
            if (reportedModel is not null)
            {
                update.ModelId = reportedModel;
            }

            yield return update;
        }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("usage tests never summarize");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("usage tests never summarize");
    }
}
