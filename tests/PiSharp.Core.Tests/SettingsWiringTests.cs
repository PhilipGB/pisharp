using PiSharp.Cli;
using PiSharp.Core;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// Conformance tests for the Phase 1 wiring: the CLI runtime must source the retry
/// budget, compaction budget (including per-model overrides), and session directory
/// from the Pi settings system instead of hard-coded values.
/// </summary>
public class SettingsWiringTests
{
    private static CliOptions Options(string workspace, bool noSession = true) => new(
        WorkingDirectory: workspace,
        Model: ModelRuntimeTestKit.Reference,
        Endpoint: null,
        ApiKey: null,
        Provider: null,
        Models: [],
        Thinking: null,
        ListModels: false,
        Offline: false,
        ContextTokens: 128_000,
        MaxOutputTokens: 1024,
        ContextTokensExplicit: false,
        MaxOutputTokensExplicit: false,
        Prompt: null,
        FilePaths: [],
        ShowHelp: false,
        ContinueSession: false,
        ResumeSession: false,
        SessionSelector: null,
        SessionName: null,
        SessionDirectory: null,
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
        NoTools: true,
        AutoRetry: true);

    private static Task<SettingsManager> Settings(string? json) =>
        SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(json, null));

    [Fact]
    public async Task RetryPolicyIsSourcedFromSettings()
    {
        using var temp = TempDirectory.Create();
        var settings = await Settings("""{"retry":{"enabled":false,"maxRetries":1,"baseDelayMs":777,"maxAgentDelayMs":999}}""");
        var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync();
        var bootstrap = await AgentFactory.CreateAsync(
            Options(temp.Path),
            projectTrusted: true,
            CancellationToken.None,
            runtime,
            state,
            temp.Path,
            settings);

        Assert.False(bootstrap.RetryPolicy.Enabled);
        // The disabled policy still carries the configured budget.
        Assert.Equal(1, bootstrap.RetryPolicy.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(777), bootstrap.RetryPolicy.BaseDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(999), bootstrap.RetryPolicy.MaximumDelay);
    }

    [Fact]
    public async Task AutoRetryFlagStillDisablesTheSettingsPolicy()
    {
        using var temp = TempDirectory.Create();
        var settings = await Settings("""{"retry":{"maxRetries":5}}""");
        var options = Options(temp.Path) with { AutoRetry = false };
        var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync();
        var bootstrap = await AgentFactory.CreateAsync(
            options,
            projectTrusted: true,
            CancellationToken.None,
            runtime,
            state,
            temp.Path,
            settings);

        Assert.False(bootstrap.RetryPolicy.Enabled);
    }

    [Fact]
    public async Task CompactionBudgetIsSourcedFromSettings()
    {
        using var temp = TempDirectory.Create();
        var settings = await Settings("""{"compaction":{"enabled":false,"reserveTokens":8000,"keepRecentTokens":12345}}""");
        var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync();
        var bootstrap = await AgentFactory.CreateAsync(
            Options(temp.Path),
            projectTrusted: true,
            CancellationToken.None,
            runtime,
            state,
            temp.Path,
            settings);
        var sessions = await SessionController.CreateAsync(
            bootstrap,
            Options(temp.Path),
            CancellationToken.None,
            settings);

        Assert.False(sessions.CompactionSettings.Enabled);
        Assert.Equal(8000, sessions.CompactionSettings.ReserveTokens);
        Assert.Equal(12345, sessions.CompactionSettings.KeepRecentTokens);
    }

    [Fact]
    public async Task PerModelCompactionOverrideAppliesForSessionModel()
    {
        using var temp = TempDirectory.Create();
        // Model "local/test-model" resolves to provider "local", modelId "test-model".
        var settings = await Settings(
            """{"compaction":{"modelOverrides":{"local/test-model":{"reserveTokens":777,"keepRecentTokens":888}}}}""");
        var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync();
        var bootstrap = await AgentFactory.CreateAsync(
            Options(temp.Path),
            projectTrusted: true,
            CancellationToken.None,
            runtime,
            state,
            temp.Path,
            settings);
        var sessions = await SessionController.CreateAsync(
            bootstrap,
            Options(temp.Path),
            CancellationToken.None,
            settings);

        Assert.Equal(777, sessions.CompactionSettings.ReserveTokens);
        Assert.Equal(888, sessions.CompactionSettings.KeepRecentTokens);
        // Un-overridden values keep the Pi defaults.
        Assert.True(sessions.CompactionSettings.Enabled);
    }

    [Fact]
    public async Task SessionDirectoryComesFromSettingsWhenCliFlagAbsent()
    {
        using var temp = TempDirectory.Create();
        var target = Path.Combine(temp.Path, "custom-sessions");
        var settings = await Settings(
            $"{{\"sessionDir\":{System.Text.Json.JsonSerializer.Serialize(target)}}}");
        var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync();
        var bootstrap = await AgentFactory.CreateAsync(
            Options(temp.Path, noSession: false),
            projectTrusted: true,
            CancellationToken.None,
            runtime,
            state,
            temp.Path,
            settings);
        var sessions = await SessionController.CreateAsync(
            bootstrap,
            Options(temp.Path, noSession: false),
            CancellationToken.None,
            settings);

        var root = Path.GetFullPath(target);
        Assert.StartsWith(root + Path.DirectorySeparatorChar, sessions.StoreDirectory);
        // A fresh session file was created under the configured root.
        Assert.Single(Directory.GetFiles(root, "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InvalidCompactionValueFallsBackToPiDefaults()
    {
        using var temp = TempDirectory.Create();
        var settings = await Settings("""{"compaction":{"reserveTokens":-1}}""");
        var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync();
        var bootstrap = await AgentFactory.CreateAsync(
            Options(temp.Path),
            projectTrusted: true,
            CancellationToken.None,
            runtime,
            state,
            temp.Path,
            settings);
        var sessions = await SessionController.CreateAsync(
            bootstrap,
            Options(temp.Path),
            CancellationToken.None,
            settings);

        Assert.True(sessions.CompactionSettings.Enabled);
        Assert.Equal(CompactionSettings.DefaultReserveTokens, sessions.CompactionSettings.ReserveTokens);
        Assert.Equal(CompactionSettings.DefaultKeepRecentTokens, sessions.CompactionSettings.KeepRecentTokens);
    }
}
