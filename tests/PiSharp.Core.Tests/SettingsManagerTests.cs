using System.Text.Json;
using PiSharp.Core;
using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// Conformance tests for the Pi-compatible settings system against the pinned Pi
/// settings-manager semantics: two scopes, deep merge, migrations, trust gating,
/// malformed-file diagnostics, reload, and modified-field save behaviour.
/// </summary>
public class SettingsManagerTests
{
    private const string GlobalPath = "/home/user/.pi/agent/settings.json";
    private const string ProjectPath = "/repo/.pi/settings.json";

    private static Dictionary<SettingsScope, string> Paths => new()
    {
        [SettingsScope.Global] = GlobalPath,
        [SettingsScope.Project] = ProjectPath,
    };

    private static Task<SettingsManager> Create(string? global, string? project, bool trusted = true) =>
        SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(global, project), trusted, Paths);

    // ------------------------------------------------------------------
    // Loading and merge precedence
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProjectSettingsOverrideGlobalScalars()
    {
        var manager = await Create(
            """{"theme":"dark","steeringMode":"one-at-a-time"}""",
            """{"theme":"light"}""");

        Assert.Equal("light", manager.GetThemeSetting());
        Assert.Equal("one-at-a-time", manager.GetSteeringMode());
        Assert.True(manager.IsProjectTrusted);
    }

    [Fact]
    public async Task NestedObjectsDeepMergeWithProjectWinningPerKey()
    {
        var manager = await Create(
            """{"compaction":{"reserveTokens":8192,"enabled":false}}""",
            """{"compaction":{"keepRecentTokens":30000}}""");

        var compaction = manager.GetSettings().Compaction;
        Assert.NotNull(compaction);
        Assert.Equal(8192, compaction?.ReserveTokens);
        Assert.Equal(30000, compaction?.KeepRecentTokens);
        Assert.False(compaction?.Enabled);
    }

    [Fact]
    public async Task ArraysAreReplacedNotMerged()
    {
        var manager = await Create(
            """{"extensions":["/a.ts","/b.ts"]}""",
            """{"extensions":["/c.ts"]}""");

        Assert.Equal(["/c.ts"], manager.GetExtensionPaths());
    }

    [Fact]
    public async Task ProjectSettingsAreIgnoredWhileUntrusted()
    {
        var manager = await Create(
            """{"theme":"dark"}""",
            """{"theme":"light","defaultModel":"project-model"}""",
            trusted: false);

        Assert.Equal("dark", manager.GetThemeSetting());
        Assert.Null(manager.GetDefaultModel());
        var project = manager.GetProjectSettings();
        Assert.Null(project.Theme);
        Assert.Null(project.DefaultModel);
    }

    [Fact]
    public async Task FixturesMergeWithPiPrecedence()
    {
        var fixture = ParityFixtureTests.FixtureRoot;
        var global = File.ReadAllText(Path.Combine(fixture, "settings", "global-settings.json"));
        var project = File.ReadAllText(Path.Combine(fixture, "settings", "project-settings.json"));
        var manager = await Create(global, project);

        var settings = manager.GetSettings();
        Assert.Equal("dark", settings.Theme);
        Assert.Equal("qwen3-27b", settings.DefaultModel);
        Assert.Equal(16384, settings.Compaction?.ReserveTokens);
        Assert.Equal(30000, settings.Compaction?.KeepRecentTokens);
        Assert.Equal(40000, settings.Compaction?.ModelOverrides?["local/long-context"]?.KeepRecentTokens);
        Assert.Equal(2, settings.Retry?.MaxRetries);
        Assert.Equal(500, settings.Retry?.BaseDelayMs);
        Assert.NotNull(settings.Packages);
        Assert.Equal(new[] { "npm:pi-example" }, settings.Packages!.Select(x => x.Source).ToArray());
        Assert.Equal(new[] { "read", "bash" }, settings.DefaultTools);
    }

    // ------------------------------------------------------------------
    // Malformed configuration behaviour
    // ------------------------------------------------------------------

    [Fact]
    public async Task MalformedGlobalSettingsProduceDiagnosticAndEmptyScope()
    {
        var manager = await Create("{ not json", """{"theme":"light"}""");

        var diagnostics = manager.DrainDiagnostics();
        Assert.Single(diagnostics);
        Assert.Equal(SettingsScope.Global, diagnostics[0].Scope);
        Assert.Equal(GlobalPath, diagnostics[0].Path);
        Assert.Contains("Invalid settings file", diagnostics[0].RenderMessage());

        // The project scope still applies and the manager stays usable.
        Assert.Equal("light", manager.GetThemeSetting());
        Assert.Empty(manager.DrainDiagnostics());
    }

    [Fact]
    public async Task NonObjectRootIsRejectedWithDiagnostic()
    {
        var manager = await Create("[1,2,3]", null);

        var diagnostics = manager.DrainDiagnostics();
        Assert.Single(diagnostics);
        Assert.Equal(SettingsScope.Global, diagnostics[0].Scope);
        Assert.Contains("object", diagnostics[0].Message);
    }

    [Fact]
    public async Task MalformedProjectSettingsKeepGlobalValuesAndReportPath()
    {
        var manager = await Create("""{"theme":"dark"}""", "{ broken");

        var diagnostics = manager.DrainDiagnostics();
        Assert.Single(diagnostics);
        Assert.Equal(SettingsScope.Project, diagnostics[0].Scope);
        Assert.Equal(ProjectPath, diagnostics[0].Path);
        Assert.Equal("dark", manager.GetThemeSetting());
    }

    [Fact]
    public async Task BomIsTolerated()
    {
        var manager = await Create("\uFEFF{\"theme\":\"dark\"}", null);

        Assert.Equal("dark", manager.GetThemeSetting());
        Assert.Empty(manager.DrainDiagnostics());
    }

    // ------------------------------------------------------------------
    // Migrations
    // ------------------------------------------------------------------

    [Fact]
    public async Task LegacySettingsMigrateToCurrentFormat()
    {
        var legacy = File.ReadAllText(
            Path.Combine(ParityFixtureTests.FixtureRoot, "settings", "settings-legacy.json"));
        var manager = await Create(legacy, null);

        var settings = manager.GetGlobalSettings();
        Assert.Equal("all", settings.SteeringMode);
        Assert.Null(settings.FollowUpMode);
        Assert.Equal("websocket", settings.Transport);
        Assert.False(settings.EnableSkillCommands);
        Assert.Equal(new[] { "./skills-extra" }, settings.Skills);
        Assert.Equal(12345, settings.Retry?.Provider?.MaxRetryDelayMs);
        Assert.Equal(30000, settings.Retry?.Provider?.TimeoutMs);
    }

    [Fact]
    public async Task MigrationDoesNotOverwriteExistingCurrentValues()
    {
        var manager = await Create(
            """{"queueMode":"all","steeringMode":"one-at-a-time","websockets":true,"transport":"sse"}""",
            null);

        Assert.Equal("one-at-a-time", manager.GetSteeringMode());
        Assert.Equal("sse", manager.GetTransport());
    }

    // ------------------------------------------------------------------
    // Reload
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReloadPicksUpExternalChanges()
    {
        var storage = new InMemorySettingsStorage("""{"theme":"dark"}""", null);
        var manager = await CreateWith(storage);
        Assert.Equal("dark", manager.GetThemeSetting());

        await storage.WriteAsync(SettingsScope.Global, """{"theme":"light","quietStartup":true}""");
        await manager.ReloadAsync();

        Assert.Equal("light", manager.GetThemeSetting());
        Assert.True(manager.GetQuietStartup());
    }

    [Fact]
    public async Task FailedReloadKeepsPreviousValuesAndReportsDiagnostic()
    {
        var storage = new InMemorySettingsStorage("""{"theme":"dark"}""", null);
        var manager = await CreateWith(storage);

        await storage.WriteAsync(SettingsScope.Global, "{ broken");
        await manager.ReloadAsync();

        Assert.Equal("dark", manager.GetThemeSetting());
        var diagnostics = manager.DrainDiagnostics();
        Assert.Single(diagnostics);
        Assert.Equal(SettingsScope.Global, diagnostics[0].Scope);
    }

    [Fact]
    public async Task TrustGainReloadsProjectSettings()
    {
        var storage = new InMemorySettingsStorage(
            """{"theme":"dark"}""",
            """{"defaultModel":"project-model"}""");
        var manager = await CreateWith(storage, trusted: false);
        Assert.Null(manager.GetDefaultModel());

        await manager.SetProjectTrustedAsync(true);

        Assert.Equal("project-model", manager.GetDefaultModel());
        Assert.Equal("dark", manager.GetThemeSetting());
    }

    [Fact]
    public async Task UntrustDropsProjectValuesFromEffectiveSettings()
    {
        var manager = await Create(null, """{"defaultModel":"project-model"}""");
        Assert.Equal("project-model", manager.GetDefaultModel());

        await manager.SetProjectTrustedAsync(false);

        Assert.Null(manager.GetDefaultModel());
    }

    // ------------------------------------------------------------------
    // Save with external-edit preservation
    // ------------------------------------------------------------------

    [Fact]
    public async Task SavePreservesExternallyEditedArrayFields()
    {
        var storage = new InMemorySettingsStorage(
            """{"theme":"dark","packages":["npm:old"]}""", null);
        var manager = await CreateWith(storage);

        // User removes the package externally, then changes an unrelated setting.
        await storage.WriteAsync(SettingsScope.Global, """{"theme":"dark","packages":[]}""");
        manager.SetTheme("light");
        await manager.SaveAsync();

        var saved = JsonDocument.Parse((await storage.ReadAsync(SettingsScope.Global))!);
        Assert.Equal("light", saved.RootElement.GetProperty("theme").GetString());
        Assert.Empty(saved.RootElement.GetProperty("packages").EnumerateArray());
    }

    [Fact]
    public async Task InMemoryChangeWinsOverExternalChangeForSameField()
    {
        var storage = new InMemorySettingsStorage("""{"theme":"dark"}""", null);
        var manager = await CreateWith(storage);

        await storage.WriteAsync(SettingsScope.Global, """{"theme":"external"}""");
        manager.SetTheme("in-memory");
        await manager.SaveAsync();

        var saved = JsonDocument.Parse((await storage.ReadAsync(SettingsScope.Global))!);
        Assert.Equal("in-memory", saved.RootElement.GetProperty("theme").GetString());
    }

    [Fact]
    public async Task SavePreservesUnknownFieldsInTheFile()
    {
        var storage = new InMemorySettingsStorage("""{"theme":"dark","futureField":{"nested":1}}""", null);
        var manager = await CreateWith(storage);

        manager.SetQuietStartup(true);
        await manager.SaveAsync();

        var saved = JsonDocument.Parse((await storage.ReadAsync(SettingsScope.Global))!);
        Assert.Equal(1, saved.RootElement.GetProperty("futureField").GetProperty("nested").GetInt32());
        Assert.True(saved.RootElement.GetProperty("quietStartup").GetBoolean());
    }

    [Fact]
    public async Task NestedSaveOnlyReplacesModifiedNestedKeys()
    {
        var storage = new InMemorySettingsStorage(
            """{"compaction":{"reserveTokens":8192,"keepRecentTokens":25000}}""", null);
        var manager = await CreateWith(storage);

        manager.SetCompactionEnabled(false);
        await manager.SaveAsync();

        var saved = JsonDocument.Parse((await storage.ReadAsync(SettingsScope.Global))!);
        var compaction = saved.RootElement.GetProperty("compaction");
        Assert.False(compaction.GetProperty("enabled").GetBoolean());
        Assert.Equal(8192, compaction.GetProperty("reserveTokens").GetInt32());
        Assert.Equal(25000, compaction.GetProperty("keepRecentTokens").GetInt32());
    }

    [Fact]
    public async Task ResetSetterRemovesKeyFromSavedFile()
    {
        var storage = new InMemorySettingsStorage("""{"shellPath":"/bin/zsh"}""", null);
        var manager = await CreateWith(storage);

        manager.SetShellPath(null);
        await manager.SaveAsync();

        var saved = JsonDocument.Parse((await storage.ReadAsync(SettingsScope.Global))!);
        Assert.False(saved.RootElement.TryGetProperty("shellPath", out _));
    }

    [Fact]
    public async Task SaveSkipsScopeThatHadALoadError()
    {
        var storage = new InMemorySettingsStorage("{ broken", null);
        var manager = await CreateWith(storage);
        _ = manager.DrainDiagnostics();

        manager.SetTheme("light");
        await manager.SaveAsync();

        // The malformed file must not be rewritten or destroyed.
        Assert.Equal("{ broken", await storage.ReadAsync(SettingsScope.Global));
    }

    [Fact]
    public async Task ProjectWriteRequiresTrust()
    {
        var manager = await Create(null, null, trusted: false);

        Assert.Throws<InvalidOperationException>(() => manager.SetProjectExtensionPaths(["./ext.ts"]));
    }

    [Fact]
    public async Task ProjectSettersPersistToProjectFileOnly()
    {
        var storage = new InMemorySettingsStorage(
            """{"theme":"dark"}""",
            """{"extensions":["./old.ts"]}""");
        var manager = await CreateWith(storage);

        manager.SetProjectExtensionPaths(["./new.ts"]);
        await manager.SaveAsync();

        var project = JsonDocument.Parse((await storage.ReadAsync(SettingsScope.Project))!);
        Assert.Equal("./new.ts", project.RootElement.GetProperty("extensions")[0].GetString());
        var global = JsonDocument.Parse((await storage.ReadAsync(SettingsScope.Global))!);
        Assert.False(global.RootElement.TryGetProperty("extensions", out _));
        Assert.Equal("./new.ts", manager.GetExtensionPaths()[0]);
    }

    // ------------------------------------------------------------------
    // Compaction budget resolution
    // ------------------------------------------------------------------

    [Fact]
    public async Task CompactionDefaultsMatchPi()
    {
        var manager = await Create(null, null);
        var settings = manager.ResolveCompactionSettings();
        Assert.True(settings.Enabled);
        Assert.Equal(16_384, settings.ReserveTokens);
        Assert.Equal(20_000, settings.KeepRecentTokens);
    }

    [Fact]
    public async Task PerModelOverridesWinForThatModelOnly()
    {
        var manager = await Create(
            """{"compaction":{"reserveTokens":8000,"modelOverrides":{"local/long-context":{"keepRecentTokens":40000}}}}""",
            null);

        var overridden = manager.ResolveCompactionSettings("local", "long-context");
        Assert.Equal(8000, overridden.ReserveTokens);
        Assert.Equal(40000, overridden.KeepRecentTokens);

        var ordinary = manager.ResolveCompactionSettings("openai", "gpt-5");
        Assert.Equal(8000, ordinary.ReserveTokens);
        Assert.Equal(20_000, ordinary.KeepRecentTokens);
    }

    [Fact]
    public async Task InvalidCompactionValuesThrowWithPiStyleMessage()
    {
        var manager = await Create("""{"compaction":{"reserveTokens":-1}}""", null);

        var exception = Assert.Throws<FormatException>(() => manager.GetCompactionReserveTokens());
        Assert.Contains("Invalid compaction.reserveTokens setting", exception.Message);
    }

    // ------------------------------------------------------------------
    // Retry resolution
    // ------------------------------------------------------------------

    [Fact]
    public async Task RetryDefaultsMatchPi()
    {
        var manager = await Create(null, null);
        var (enabled, maxRetries, baseDelay, maxDelay) = manager.GetRetrySettings();
        Assert.True(enabled);
        Assert.Equal(3, maxRetries);
        Assert.Equal(2000, baseDelay);
        Assert.Equal(60_000, maxDelay);

        var policy = manager.GetRetryPolicy();
        Assert.True(policy.Enabled);
        Assert.Equal(3, policy.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), policy.BaseDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(60_000), policy.MaximumDelay);
    }

    [Fact]
    public async Task RetrySettingsOverrideAndDisable()
    {
        var manager = await Create("""{"retry":{"enabled":false,"maxRetries":1}}""", null);
        var policy = manager.GetRetryPolicy();
        Assert.False(policy.Enabled);
        Assert.Equal(1, policy.MaxRetries);

        // Non-persisted override (CLI --no-auto-retry equivalent) wins for this process.
        var cli = await Create("""{"retry":{"maxRetries":5}}""", null);
        cli.ApplyOverrides(new PiSettings { Retry = new PiRetrySettings { Enabled = false } });
        Assert.False(cli.GetRetryPolicy().Enabled);
    }

    // ------------------------------------------------------------------
    // Queue, trust-default, and misc typed getters
    // ------------------------------------------------------------------

    [Fact]
    public async Task QueueModesResolveWithPiDefaultsAndValidation()
    {
        var manager = await Create("""{"steeringMode":"all"}""", """{"followUpMode":"all"}""");
        Assert.Equal("all", manager.GetSteeringMode());
        Assert.Equal("all", manager.GetFollowUpMode());

        var fallback = await Create("""{"steeringMode":"bogus"}""", null);
        Assert.Equal("one-at-a-time", fallback.GetSteeringMode());

        var validating = await Create(null, null);
        Assert.Throws<FormatException>(() => validating.SetSteeringMode("sometimes"));
    }

    [Fact]
    public async Task DefaultProjectTrustComesFromGlobalScopeOnly()
    {
        var manager = await Create("""{"defaultProjectTrust":"never"}""", """{"defaultProjectTrust":"always"}""");
        Assert.Equal(DefaultProjectTrust.Never, manager.GetDefaultProjectTrust());

        var invalid = await Create("""{"defaultProjectTrust":"sometimes"}""", null);
        Assert.Equal(DefaultProjectTrust.Ask, invalid.GetDefaultProjectTrust());
    }

    [Fact]
    public async Task ThemeSelectionSeparatesFixedAndAutomatic()
    {
        var fixedTheme = await Create("""{"theme":"dark"}""", null);
        Assert.Equal("dark", fixedTheme.GetTheme());

        var automatic = await Create("""{"theme":"auto/dark"}""", null);
        Assert.Equal("auto/dark", automatic.GetThemeSetting());
        Assert.Null(automatic.GetTheme());
    }

    [Fact]
    public async Task HttpIdleTimeoutParsesNumbersAndDisabled()
    {
        var manager = await Create("""{"httpIdleTimeoutMs":"disabled"}""", null);
        Assert.Equal(0, manager.GetHttpIdleTimeoutMs());

        var numeric = await Create("""{"httpIdleTimeoutMs":15000}""", null);
        Assert.Equal(15000, numeric.GetHttpIdleTimeoutMs());

        var defaults = await Create(null, null);
        Assert.Equal(SettingsManager.DefaultHttpIdleTimeoutMs, defaults.GetHttpIdleTimeoutMs());

        var invalid = await Create("""{"httpIdleTimeoutMs":-5}""", null);
        Assert.Throws<FormatException>(() => invalid.GetHttpIdleTimeoutMs());
    }

    [Fact]
    public async Task ThinkingLevelValidationRejectsUnknownLevels()
    {
        var manager = await Create(null, null);
        Assert.Throws<FormatException>(() => manager.SetDefaultThinkingLevel("ultra"));
        foreach (var level in SettingsManager.ThinkingLevels)
        {
            manager.SetDefaultThinkingLevel(level);
            Assert.Equal(level, manager.GetDefaultThinkingLevel());
        }
    }

    [Fact]
    public async Task SessionDirExpandsTilde()
    {
        var manager = await Create("""{"sessionDir":"~/sessions"}""", null);
        var expected = Path.GetFullPath(Path.Combine(ProjectTrustPath.GetHomeDirectory(), "sessions"));
        Assert.Equal(expected, manager.GetSessionDir());
    }

    [Fact]
    public async Task PackageSourcesAcceptStringAndObjectForms()
    {
        var manager = await Create(
            """{"packages":["npm:simple",{"source":"npm:filtered","extensions":["extensions/oracle.ts"],"skills":[]}]}""",
            null);

        var packages = manager.GetPackages();
        Assert.Equal(2, packages.Count);
        Assert.Equal("npm:simple", packages[0].Source);
        Assert.Null(packages[0].Extensions);
        Assert.Equal("npm:filtered", packages[1].Source);
        Assert.Equal(new[] { "extensions/oracle.ts" }, packages[1].Extensions);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Task<SettingsManager> CreateWith(
        InMemorySettingsStorage storage,
        bool trusted = true) =>
        SettingsManager.CreateFromStorageAsync(storage, trusted, Paths);
}
