using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class UserSettingsTests
{
    [Fact]
    public async Task UserSettingsProvideValidatedDefaultsBelowCliAndEnvironmentOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
                "{\"defaultProvider\":\"mistral\",\"defaultModel\":\"mistral-large-latest\",\"defaultThinkingLevel\":\"HIGH\",\"defaultTools\":[\"read\",\"grep\"],\"sessionDir\":\"/tmp/pisharp-sessions\"}");
            var settings = await UserSettings.LoadAsync(root, _ => null);
            var defaults = settings.ApplyDefaults(CliArguments.Parse(["--print", "hello"]), _ => null);
            Assert.Equal("mistral", defaults.Provider);
            Assert.Equal("mistral-large-latest", defaults.ModelOverride);
            Assert.Equal("high", defaults.Thinking);
            Assert.Equal(["read", "grep"], defaults.Tools);
            Assert.Equal("/tmp/pisharp-sessions", settings.SessionDirectory);

            var explicitOptions = settings.ApplyDefaults(CliArguments.Parse(["--provider", "openai", "--model", "gpt-custom", "--thinking", "low", "--tools", "write"]), _ => null);
            Assert.Equal("openai", explicitOptions.Provider);
            Assert.Equal("gpt-custom", explicitOptions.ModelOverride);
            Assert.Equal("low", explicitOptions.Thinking);
            Assert.Equal(["write"], explicitOptions.Tools);
            var disabledTools = settings.ApplyDefaults(CliArguments.Parse(["--no-tools"]), _ => null);
            Assert.Null(disabledTools.Tools);

            var continuation = settings.ApplyDefaults(CliArguments.Parse(["--continue"]), _ => null, preserveSessionModel: true);
            Assert.Null(continuation.Provider);
            Assert.Null(continuation.ModelOverride);
            Assert.Equal("high", continuation.Thinking);
            var listing = settings.ApplyDefaults(CliArguments.Parse(["--list-models"]), _ => null, preserveSessionModel: true);
            Assert.Null(listing.Provider);
            Assert.Null(listing.ModelOverride);

            var environmentOverride = settings.ApplyDefaults(CliArguments.Parse(["--provider", "mistral"]),
                name => name == "PISHARP_MISTRAL_MODEL" ? "mistral-small-latest" : null);
            Assert.Null(environmentOverride.ModelOverride);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CompactionSettingsApplyToKnownModelAndExplicitEnvironmentWins()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-compaction-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
                "{\"compaction\":{\"enabled\":true,\"reserveTokens\":2048,\"keepRecentTokens\":512}}");
            var settings = await UserSettings.LoadAsync(root, _ => null);
            Assert.Null(settings.ResolveCompaction(null, _ => null));
            Assert.Equal(7952, settings.ResolveCompaction(10000, _ => null)!.TriggerTokens);
            Assert.Equal(512, settings.ResolveCompaction(10000, _ => null)!.KeepRecentTokens);
            Assert.Equal(4000, settings.ResolveCompaction(10000, name => name switch
            {
                "PISHARP_CONTEXT_WINDOW_TOKENS" => "5000",
                "PISHARP_CONTEXT_RESERVE_TOKENS" => "1000",
                _ => null
            })!.TriggerTokens);
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{\"compaction\":{\"enabled\":false}}");
            var disabled = await UserSettings.LoadAsync(root, _ => null);
            Assert.Null(disabled.ResolveCompaction(10000, _ => null));
            Assert.Equal(4000, disabled.ResolveCompaction(10000, name => name switch
            {
                "PISHARP_CONTEXT_WINDOW_TOKENS" => "5000",
                "PISHARP_CONTEXT_RESERVE_TOKENS" => "1000",
                _ => null
            })!.TriggerTokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TrustedProjectSettingsOverlayUserDefaultsWithoutDiscardingNestedCompaction()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-project-settings-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(project, ".pi"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
                "{\"defaultProvider\":\"mistral\",\"defaultTools\":[\"read\"],\"compaction\":{\"enabled\":false,\"reserveTokens\":1000}}");
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "settings.json"),
                "{\"defaultProvider\":\"openrouter\",\"compaction\":{\"enabled\":true}}");
            var global = await UserSettings.LoadAsync(agent, _ => null);
            Assert.Null(global.ResolveCompaction(10000, _ => null));
            var merged = global.Overlay(await UserSettings.LoadProjectAsync(project));
            Assert.Equal("openrouter", merged.DefaultProvider);
            Assert.Equal(["read"], merged.DefaultTools);
            Assert.Equal(9000, merged.ResolveCompaction(10000, _ => null)!.TriggerTokens);
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "settings.json"), "{\"compaction\":{\"reserveTokens\":2000}}");
            Assert.Null(global.Overlay(await UserSettings.LoadProjectAsync(project)).ResolveCompaction(10000, _ => null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExactModelCompactionOverridesMergeAcrossScopes()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-model-compaction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
                "{\"compaction\":{\"reserveTokens\":1000,\"modelOverrides\":{\"openai/large\":{\"reserveTokens\":3000,\"keepRecentTokens\":700},\"other/one\":{\"reserveTokens\":2500}}}}");
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"),
                "{\"compaction\":{\"modelOverrides\":{\"openai/large\":{\"keepRecentTokens\":800}}}}");
            var global = await UserSettings.LoadAsync(root, _ => null);
            var merged = global.Overlay(await UserSettings.LoadProjectAsync(root));
            Assert.Equal(7000, merged.ResolveCompaction(10000, _ => null, "openai/large")!.TriggerTokens);
            Assert.Equal(800, merged.ResolveCompaction(10000, _ => null, "openai/large")!.KeepRecentTokens);
            Assert.Equal(7500, merged.ResolveCompaction(10000, _ => null, "other/one")!.TriggerTokens);
            Assert.Equal(9000, merged.ResolveCompaction(10000, _ => null, "openai/other")!.TriggerTokens);
            Assert.Equal(8000, merged.ResolveCompaction(10000, name => name switch
            {
                "PISHARP_CONTEXT_WINDOW_TOKENS" => "9000",
                "PISHARP_CONTEXT_RESERVE_TOKENS" => "1000",
                _ => null
            }, "openai/large")!.TriggerTokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ImageBlockingIsScopedAndValidated()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-image-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{\"images\":{\"blockImages\":true}}");
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"), "{\"images\":{\"blockImages\":false}}");
            var global = await UserSettings.LoadAsync(root, _ => null);
            Assert.True(global.BlockImages);
            Assert.False(global.Overlay(await UserSettings.LoadProjectAsync(root)).BlockImages);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HideThinkingBlockRequiresBooleanAndTrustedProjectCanOverrideDisplay()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-thinking-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        try
        {
            var globalPath = Path.Combine(root, "settings.json");
            var projectPath = Path.Combine(root, ".pi", "settings.json");
            await File.WriteAllTextAsync(globalPath, "{\"hideThinkingBlock\":true}");
            var global = await UserSettings.LoadAsync(root, _ => null);
            Assert.True(global.HideThinkingBlock);
            await File.WriteAllTextAsync(projectPath, "{\"hideThinkingBlock\":false}");
            Assert.False(global.Overlay(await UserSettings.LoadProjectAsync(root)).HideThinkingBlock);
            Assert.True(global.Overlay(new UserSettings()).HideThinkingBlock);
            await File.WriteAllTextAsync(globalPath, "{\"hideThinkingBlock\":\"true\"}");
            await Assert.ThrowsAsync<InvalidDataException>(() => UserSettings.LoadAsync(root, _ => null));
            await File.WriteAllTextAsync(globalPath, "{\"hideThinkingBlock\":true,\"hideThinkingBlock\":false}");
            await Assert.ThrowsAsync<InvalidDataException>(() => UserSettings.LoadAsync(root, _ => null));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task DefaultProjectTrustIsGlobalOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-default-trust-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{\"defaultProjectTrust\":\"always\"}");
            var global = await UserSettings.LoadAsync(root, _ => null);
            Assert.Equal("always", global.DefaultProjectTrust);
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"), "{\"defaultProjectTrust\":\"never\"}");
            await Assert.ThrowsAsync<InvalidDataException>(() => UserSettings.LoadProjectAsync(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{\"defaultThinkingLevel\":\"ultra\"}")]
    [InlineData("{\"defaultProjectTrust\":\"maybe\"}")]
    [InlineData("{\"defaultProjectTrust\":true}")]
    [InlineData("{\"unknown\":\"value\"}")]
    [InlineData("{\"defaultModel\":42}")]
    [InlineData("{\"defaultTools\":\"read\"}")]
    [InlineData("{\"defaultTools\":[\"unknown\"]}")]
    [InlineData("{\"defaultTools\":[\"read\",\"read\"]}")]
    [InlineData("{\"defaultModel\":\"a\",\"defaultModel\":\"b\"}")]
    [InlineData("{\"compaction\":{\"enabled\":\"false\"}}")]
    [InlineData("{\"compaction\":{\"reserveTokens\":-1}}")]
    [InlineData("{\"compaction\":{\"reserveTokens\":2,\"reserveTokens\":3}}")]
    [InlineData("{\"compaction\":{\"keepRecentTokens\":-1}}")]
    [InlineData("{\"compaction\":{\"keepRecentTokens\":3,\"keepRecentTokens\":4}}")] // duplicate fails
    [InlineData("{\"compaction\":[]}")]
    [InlineData("{\"compaction\":{\"modelOverrides\":{\"bare-model\":{\"reserveTokens\":10}}}}")]
    [InlineData("{\"compaction\":{\"modelOverrides\":{\"p/m\":{\"enabled\":false}}}}")]
    [InlineData("{\"compaction\":{\"modelOverrides\":{\"p/m\":{\"reserveTokens\":1},\"p/m\":{\"reserveTokens\":2}}}}")]
    [InlineData("{\"compaction\":{\"modelOverrides\":[]}}")]
    [InlineData("{\"images\":{\"blockImages\":\"true\"}}")]
    [InlineData("{\"images\":{\"blockImages\":true,\"blockImages\":false}}")]
    [InlineData("{\"images\":{\"autoResize\":true}}")]
    [InlineData("{\"images\":[]}")]
    public async Task InvalidSettingsFailClosed(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-settings-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), json);
            await Assert.ThrowsAnyAsync<Exception>(() => UserSettings.LoadAsync(root, _ => null));
        }
        finally { Directory.Delete(root, true); }
    }
}
