using PiSharp.Cli;

namespace PiSharp.Tests;

/// <summary>
/// `branchSummary.skipPrompt` is pinned to Pi's settings boundary: settings files are loaded
/// without schema validation, scopes deep-merge by property presence, and the tree flow asks
/// the question only when the merged value is falsey.
/// </summary>
public sealed class BranchSummarySkipPromptSettingTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-skip-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<UserSettings> LoadAsync(string root, string json)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), json);
        return await UserSettings.LoadAsync(root, _ => null);
    }

    private static async Task<UserSettings> LoadProjectAsync(string root, string json)
    {
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, ".pi"));
        await File.WriteAllTextAsync(Path.Combine(project, ".pi", "settings.json"), json);
        return await UserSettings.LoadProjectAsync(project);
    }

    [Fact]
    public async Task UnsetResolvesToTheDefaultPromptPath()
    {
        var root = NewRoot();
        try
        {
            var settings = await LoadAsync(root, "{\"quietStartup\":true}");
            Assert.Null(settings.BranchSummary);
            Assert.False(settings.EffectiveBranchSummary.SkipsSummaryPrompt);
            Assert.Equal(BranchSummarySettings.DefaultReserveTokens,
                settings.EffectiveBranchSummary.EffectiveReserveTokens);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public async Task ExplicitBooleanValuesMatchThePinnedPromptDecision(string stored, bool skips)
    {
        var root = NewRoot();
        try
        {
            var settings = await LoadAsync(root, "{\"branchSummary\":{\"skipPrompt\":" + stored + "}}");
            Assert.Equal(stored, settings.BranchSummary!.Value.Settings!.SkipPrompt!.Value.Raw);
            Assert.Equal(skips, settings.EffectiveBranchSummary.SkipsSummaryPrompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ProjectReserveTokensDoesNotEraseTheInheritedSkipPrompt()
    {
        var root = NewRoot();
        try
        {
            var user = await LoadAsync(root,
                "{\"branchSummary\":{\"skipPrompt\":true,\"reserveTokens\":4096}}");
            var project = await LoadProjectAsync(root, "{\"branchSummary\":{\"reserveTokens\":1234}}");
            var merged = user.Overlay(project);

            Assert.True(merged.EffectiveBranchSummary.SkipsSummaryPrompt);
            Assert.Equal(1234, merged.EffectiveBranchSummary.EffectiveReserveTokens);
            Assert.Equal("true", merged.BranchSummary!.Value.Settings!.SkipPrompt!.Value.Raw);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public async Task TrustedProjectBooleanOverridesTheUserValue(bool userValue, bool projectValue, bool prompts)
    {
        var root = NewRoot();
        try
        {
            var user = await LoadAsync(root,
                "{\"branchSummary\":{\"skipPrompt\":" + (userValue ? "true" : "false") + "}}");
            var project = await LoadProjectAsync(root,
                "{\"branchSummary\":{\"skipPrompt\":" + (projectValue ? "true" : "false") + "}}");
            Assert.Equal(prompts, !user.Overlay(project).EffectiveBranchSummary.SkipsSummaryPrompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TrustedProjectExplicitNullReplacesTheUserValueAndResolvesToTheDefault()
    {
        var root = NewRoot();
        try
        {
            var user = await LoadAsync(root, "{\"branchSummary\":{\"skipPrompt\":true}}");
            var project = await LoadProjectAsync(root, "{\"branchSummary\":{\"skipPrompt\":null}}");
            var merged = user.Overlay(project);

            Assert.Equal("null", merged.EffectiveBranchSummary.SkipPrompt!.Value.Raw);
            Assert.False(merged.EffectiveBranchSummary.SkipPrompt!.Value.SkipsPrompt);
            Assert.False(merged.EffectiveBranchSummary.SkipsSummaryPrompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UntrustedProjectContributesNothing()
    {
        var root = NewRoot();
        try
        {
            var user = await LoadAsync(root, "{\"branchSummary\":{\"skipPrompt\":true}}");
            await LoadProjectAsync(root, "{\"branchSummary\":{\"skipPrompt\":false}}");

            // Project settings load only after trust, so an untrusted project overlays nothing.
            Assert.True(user.Overlay(new UserSettings()).EffectiveBranchSummary.SkipsSummaryPrompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"bad\"")]
    [InlineData("42")]
    [InlineData("[]")]
    public async Task ProjectNonObjectReplacesTheUserObjectAndReadsAsAbsent(string stored)
    {
        var root = NewRoot();
        try
        {
            var user = await LoadAsync(root, "{\"branchSummary\":{\"skipPrompt\":true}}");
            var project = await LoadProjectAsync(root, "{\"branchSummary\":" + stored + "}");
            var merged = user.Overlay(project);

            Assert.NotNull(merged.BranchSummary);
            Assert.Null(merged.BranchSummary!.Value.Settings);
            Assert.False(merged.EffectiveBranchSummary.SkipsSummaryPrompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"bad\"")]
    [InlineData("42")]
    [InlineData("[]")]
    public async Task UserNonObjectParentReadsAsAbsent(string stored)
    {
        var root = NewRoot();
        try
        {
            var settings = await LoadAsync(root, "{\"branchSummary\":" + stored + "}");
            Assert.NotNull(settings.BranchSummary);
            Assert.Null(settings.BranchSummary!.Value.Settings);
            Assert.False(settings.EffectiveBranchSummary.SkipsSummaryPrompt);
            Assert.Equal(BranchSummarySettings.DefaultReserveTokens,
                settings.EffectiveBranchSummary.EffectiveReserveTokens);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    // Pinned Pi returns the stored value and the tree flow tests it for JavaScript truthiness.
    [InlineData("\"false\"", false)]
    [InlineData("\"\"", true)]
    [InlineData("1", false)]
    [InlineData("0", true)]
    [InlineData("2.5", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    [InlineData("{\"nested\":false}", false)]
    [InlineData("null", true)]
    public async Task StoredMalformedValuesUseJavaScriptTruthiness(string stored, bool prompts)
    {
        var root = NewRoot();
        try
        {
            var settings = await LoadAsync(root, "{\"branchSummary\":{\"skipPrompt\":" + stored + "}}");
            Assert.Equal(stored, settings.EffectiveBranchSummary.SkipPrompt!.Value.Raw);
            Assert.Equal(prompts, !settings.EffectiveBranchSummary.SkipsSummaryPrompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"\"", true)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    public async Task MalformedTrustedProjectValueReplacesTheValidUserValue(string stored, bool prompts)
    {
        var root = NewRoot();
        try
        {
            var user = await LoadAsync(root, "{\"branchSummary\":{\"skipPrompt\":" +
                (prompts ? "true" : "false") + "}}");
            var project = await LoadProjectAsync(root, "{\"branchSummary\":{\"skipPrompt\":" + stored + "}}");
            Assert.Equal(prompts, !user.Overlay(project).EffectiveBranchSummary.SkipsSummaryPrompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    // Pinned Pi never validates the loaded object, so unrelated siblings are ignored.
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnknownSiblingsDoNotAffectThePromptDecision(bool stored, bool prompts)
    {
        var root = NewRoot();
        try
        {
            var settings = await LoadAsync(root, "{\"branchSummary\":{\"skipPrompt\":" +
                (stored ? "true" : "false") + ",\"futureSetting\":123}}");
            Assert.Equal(prompts, !settings.EffectiveBranchSummary.SkipsSummaryPrompt);
            Assert.Equal(stored ? "true" : "false", settings.EffectiveBranchSummary.SkipPrompt!.Value.Raw);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    // JSON.parse resolves a duplicated key with the later value.
    [InlineData("false,\"skipPrompt\":true", false)]
    [InlineData("true,\"skipPrompt\":false", true)]
    public async Task DuplicateSkipPromptUsesTheLaterValue(string entries, bool prompts)
    {
        var root = NewRoot();
        try
        {
            var settings = await LoadAsync(root, "{\"branchSummary\":{\"skipPrompt\":" + entries + "}}");
            Assert.Equal(prompts, !settings.EffectiveBranchSummary.SkipsSummaryPrompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TrustedProjectUnknownSiblingsKeepTheInheritedSkipPrompt()
    {
        var root = NewRoot();
        try
        {
            var user = await LoadAsync(root,
                "{\"branchSummary\":{\"skipPrompt\":true,\"futureSetting\":1}}");
            var project = await LoadProjectAsync(root, "{\"branchSummary\":{\"futureSetting\":2}}");
            Assert.True(user.Overlay(project).EffectiveBranchSummary.SkipsSummaryPrompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TrustedProjectUnknownSiblingsDoNotBlockAnExplicitOverride()
    {
        var root = NewRoot();
        try
        {
            var user = await LoadAsync(root, "{\"branchSummary\":{\"skipPrompt\":true}}");
            var project = await LoadProjectAsync(root,
                "{\"branchSummary\":{\"skipPrompt\":false,\"futureSetting\":123}}");
            Assert.False(user.Overlay(project).EffectiveBranchSummary.SkipsSummaryPrompt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("{}", 16384)]
    [InlineData("{\"branchSummary\":{}}", 16384)]
    [InlineData("{\"branchSummary\":{\"reserveTokens\":1234}}", 1234)]
    [InlineData("{\"branchSummary\":{\"reserveTokens\":0}}", 0)]
    [InlineData("{\"branchSummary\":{\"skipPrompt\":true}}", 16384)]
    public async Task ReserveTokensKeepsItsExistingBehaviour(string stored, int expected)
    {
        var root = NewRoot();
        try
        {
            var settings = await LoadAsync(root, stored);
            Assert.Equal(expected, settings.EffectiveBranchSummary.EffectiveReserveTokens);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ReserveTokensStillFailsClosedForInvalidValues()
    {
        var root = NewRoot();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                LoadAsync(root, "{\"branchSummary\":{\"reserveTokens\":-1}}"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
