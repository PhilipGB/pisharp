using System.Text.Json.Nodes;
using PiSharp.Cli;
using PiSharp.Cli.Tui;
using PiSharp.Cli.Sessions;
using PiSharp.ResourceDiscoveryExtension;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ProjectRuntimeContextTests
{
    [Fact]
    public void SkillPromptReaderUsesEffectiveDeclaredToolSetAndPiPreference()
    {
        var arguments = CliArguments.Parse([]);
        Assert.Equal("read", ProjectRuntimeContext.ResolveSkillFileReadTool(arguments, ["bash", "read"]));
        Assert.Equal("bash", ProjectRuntimeContext.ResolveSkillFileReadTool(arguments, ["bash"]));
        Assert.Null(ProjectRuntimeContext.ResolveSkillFileReadTool(arguments, ["write", "edit"]));
        Assert.Null(ProjectRuntimeContext.ResolveSkillFileReadTool(CliArguments.Parse(["--no-tools"]), ["read"]));
        Assert.Equal("bash", ProjectRuntimeContext.ResolveSkillFileReadTool(CliArguments.Parse(["--tools", "bash"])));
    }

    [Fact]
    public async Task ProjectTrustExtensionRunsBeforeSavedDefaultAndIsReusedAfterTrust()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp project trust extension " + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        var piDirectory = Path.Combine(project, ".pi");
        var projectExtensionDirectory = Path.Combine(piDirectory, "extensions");
        var extensionPath = typeof(ResourceDiscoveryFixture).Assembly.Location;
        var loadLog = Path.Combine(root, "extension-loads.log");
        var priorLoadLog = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG");
        Directory.CreateDirectory(piDirectory);
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(projectExtensionDirectory);
        File.Copy(extensionPath, Path.Combine(projectExtensionDirectory, "project-extension.dll"));
        await File.WriteAllTextAsync(Path.Combine(piDirectory, "settings.json"), "{\"defaultTools\":[\"read\"]}");
        await File.WriteAllTextAsync(Path.Combine(piDirectory, "project-trust-extension.json"),
            "{\"decision\":\"yes\",\"remember\":true}");
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{\"defaultProjectTrust\":\"never\"}");

        try
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", loadLog);
            var arguments = CliArguments.Parse(["--extension", extensionPath]);
            var trust = new ProjectTrust(agent);
            await trust.SetAsync(project, false);

            using var configuration = await ProjectRuntimeConfiguration.LoadAsync(project, agent, arguments,
                trust, interactiveTrust: false, TextReader.Null, TextWriter.Null);

            Assert.True(configuration.Trusted);
            Assert.True(await trust.GetAsync(project));
            Assert.Equal(["configured", "trust-undecided:false", "trust-decision:false"],
                await File.ReadAllLinesAsync(loadLog));

            using var runtime = await ProjectRuntimeContext.LoadAsync(configuration, agent, arguments, null);

            var loadedExtensions = runtime.Extensions.LoadedExtensions;
            Assert.Equal(2, loadedExtensions.Count);
            Assert.Contains(loadedExtensions, extension => extension.Scope == "project");
            Assert.Contains(loadedExtensions, extension => extension.Source == "cli");
            Assert.Equal(["configured", "trust-undecided:false", "trust-decision:false", "configured"],
                await File.ReadAllLinesAsync(loadLog));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", priorLoadLog);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectTrustExtensionErrorsAreReportedBeforeSavedDefaultFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp project trust error " + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        var piDirectory = Path.Combine(project, ".pi");
        var extensionPath = typeof(ResourceDiscoveryFixture).Assembly.Location;
        Directory.CreateDirectory(piDirectory);
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(piDirectory, "settings.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(piDirectory, "project-trust-extension.json"),
            "{\"decision\":\"throw\",\"remember\":true}");
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{\"defaultProjectTrust\":\"never\"}");

        try
        {
            var arguments = CliArguments.Parse(["--no-extensions", "--extension", extensionPath]);
            var trust = new ProjectTrust(agent);
            using var output = new StringWriter();

            using var configuration = await ProjectRuntimeConfiguration.LoadAsync(project, agent, arguments,
                trust, interactiveTrust: false, TextReader.Null, output);

            Assert.False(configuration.Trusted);
            Assert.Null(await trust.GetAsync(project));
            Assert.Contains($"Extension \"{extensionPath}\" project_trust error: fixture trust hook failure",
                output.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ProjectTrustExtensionUiMethodsReceivePiContextAndReturnInteractiveValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp project trust extension ui " + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        var piDirectory = Path.Combine(project, ".pi");
        var extensionPath = typeof(ResourceDiscoveryFixture).Assembly.Location;
        var loadLog = Path.Combine(root, "extension-loads.log");
        var priorLoadLog = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG");
        Directory.CreateDirectory(piDirectory);
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(piDirectory, "settings.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(piDirectory, "project-trust-extension.json"),
            "{\"exerciseUi\":true,\"decision\":\"yes\",\"remember\":true}");
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{\"defaultProjectTrust\":\"never\"}");

        try
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", loadLog);
            var ui = new RecordingProjectTrustUi();
            using var configuration = await ProjectRuntimeConfiguration.LoadAsync(project, agent,
                CliArguments.Parse(["--extension", extensionPath]), new ProjectTrust(agent),
                interactiveTrust: true, TextReader.Null, TextWriter.Null, extensionUi: ui);

            Assert.True(configuration.Trusted);
            Assert.Equal(["select:Trust option:Skip|Continue", "confirm:Continue?:Trust project resources?",
                "input:Project label:approved", "notify:warning:Project trust extension notification"], ui.Calls);
            Assert.Equal(["configured", "trust-undecided:true", "trust-decision:true",
                "trust-ui:tui:true:Continue:true:approved"], await File.ReadAllLinesAsync(loadLog));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", priorLoadLog);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectTrustExtensionUiDefaultsMatchHeadlessPiBehavior()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp project trust extension headless ui " + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        var piDirectory = Path.Combine(project, ".pi");
        var extensionPath = typeof(ResourceDiscoveryFixture).Assembly.Location;
        var loadLog = Path.Combine(root, "extension-loads.log");
        var priorLoadLog = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG");
        Directory.CreateDirectory(piDirectory);
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(piDirectory, "settings.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(piDirectory, "project-trust-extension.json"),
            "{\"exerciseUi\":true,\"decision\":\"yes\"}");
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{\"defaultProjectTrust\":\"never\"}");

        try
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", loadLog);
            using var output = new StringWriter();
            using var configuration = await ProjectRuntimeConfiguration.LoadAsync(project, agent,
                CliArguments.Parse(["--print", "--extension", extensionPath]), new ProjectTrust(agent),
                interactiveTrust: false, TextReader.Null, output);

            Assert.True(configuration.Trusted);
            Assert.Equal(["configured", "trust-undecided:false", "trust-decision:false",
                "trust-ui:print:false::false:"], await File.ReadAllLinesAsync(loadLog));
            Assert.Equal(string.Empty, output.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", priorLoadLog);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectTrustStartupUiUsesPiHeadlessDefaultsAndNotificationMode()
    {
        using var output = new StringWriter();
        using var printError = new StringWriter();
        var printUi = new ProjectTrustStartupUi(hasUserInterface: false, mode: "print", output: output, error: printError);

        Assert.Null(await printUi.SelectAsync("Choose", ["one", "two"]));
        Assert.False(await printUi.ConfirmAsync("Confirm", "Continue?"));
        Assert.Null(await printUi.InputAsync("Value", "placeholder"));
        printUi.Notify("startup warning", "warning");
        Assert.Contains("startup warning", printError.ToString());

        using var interactiveError = new StringWriter();
        var interactiveUi = new ProjectTrustStartupUi(hasUserInterface: false, mode: "interactive",
            output: output, error: interactiveError);
        interactiveUi.Notify("interactive notifications are silent");
        Assert.Equal(string.Empty, interactiveError.ToString());
    }

    [Fact]
    public async Task ExtensionResourcesDiscoverOnStartupAndAreReplacedOnReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp extension resources " + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        var extensionPath = typeof(ResourceDiscoveryFixture).Assembly.Location;
        Assert.True(File.Exists(extensionPath));
        Directory.CreateDirectory(agent);
        await WriteResourcesAsync(project, "startup", "startup-skill", "startup-review", "startup-theme");
        await WriteResourcesAsync(project, "reloaded", "reloaded-skill", "reloaded-review", "reloaded-theme");

        try
        {
            var arguments = CliArguments.Parse(["--no-extensions", "--no-skills", "--no-prompt-templates",
                "--extension", extensionPath]);
            var trust = new ProjectTrust(agent);
            var configuration = await ProjectRuntimeConfiguration.LoadAsync(project, agent, arguments,
                trust, interactiveTrust: false, TextReader.Null, TextWriter.Null, trustedOverride: true);

            using var startup = await ProjectRuntimeContext.LoadAsync(configuration, agent, arguments, null);
            var startupSkill = Assert.Single(startup.Resources.Skills);
            Assert.Equal("startup-skill", startupSkill.Name);
            Assert.Equal("extension:dynamic-resources", startupSkill.SourceInfo.Source);
            Assert.Equal("temporary", startupSkill.SourceInfo.Scope);
            Assert.Equal("top-level", startupSkill.SourceInfo.Origin);
            Assert.Equal(Path.GetDirectoryName(extensionPath), startupSkill.SourceInfo.BaseDir);
            var startupPrompt = Assert.Single(startup.Resources.Prompts);
            Assert.Equal("startup-review", startupPrompt.Name);
            Assert.Equal("extension:dynamic-resources", startupPrompt.SourceInfo.Source);
            var startupThemePath = Assert.Single(startup.ExtensionResources.ThemePaths);
            Assert.Equal("extension:dynamic-resources", startupThemePath.SourceInfo.Source);
            Assert.Empty(startup.ExtensionResources.Errors);
            AssertTheme(startup.ExtensionResources, project, "startup-theme", "#123456");

            using var reloaded = await ProjectRuntimeContext.LoadAsync(configuration, agent, arguments, null,
                resourceDiscoveryReason: ExtensionResourceDiscoveryReason.Reload);
            Assert.Equal("reloaded-skill", Assert.Single(reloaded.Resources.Skills).Name);
            var reloadedPrompt = Assert.Single(reloaded.Resources.Prompts);
            Assert.Equal("reloaded-review", reloadedPrompt.Name);
            Assert.Equal("extension:dynamic-resources", reloadedPrompt.SourceInfo.Source);
            Assert.Empty(reloaded.ExtensionResources.Errors);
            AssertTheme(reloaded.ExtensionResources, project, "reloaded-theme", "#654321");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LoadsProjectScopedSettingsResourcesAndStoreUnderTheTrustDecision()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-project-runtime-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        var privateSkill = Path.Combine(project, ".pi", "skills", "private");
        var configuredSkill = Path.Combine(project, ".pi", "configured-skills", "first");
        var configuredPrompts = Path.Combine(project, ".pi", "configured-prompts");
        Directory.CreateDirectory(privateSkill);
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
            "{\"shellCommandPrefix\":\"export PISHARP_PREFIX=user\",\"extensions\":[\"builtin:mcp\",\"builtin:tool-search\"]}");
        await File.WriteAllTextAsync(Path.Combine(project, ".pi", "settings.json"),
            "{\"images\":{\"blockImages\":true},\"sessionDir\":\"custom-sessions\",\"terminal\":{\"trueColor\":true},\"enableSkillCommands\":false,\"shellCommandPrefix\":\"export PISHARP_PREFIX=project\",\"skills\":[\"configured-skills\"],\"prompts\":[\"configured-prompts\"]}");
        await File.WriteAllTextAsync(Path.Combine(privateSkill, "SKILL.md"),
            "---\nname: private-guide\ndescription: A trusted project guide\n---\nUse this guide.");
        Directory.CreateDirectory(configuredSkill);
        Directory.CreateDirectory(configuredPrompts);
        await File.WriteAllTextAsync(Path.Combine(configuredSkill, "SKILL.md"),
            "---\nname: configured-first\ndescription: A configured project guide\n---\nUse this configured guide.");
        await File.WriteAllTextAsync(Path.Combine(configuredPrompts, "configured-review.md"), "Review configured resources.");

        try
        {
            var trust = new ProjectTrust(agent);
            var arguments = CliArguments.Parse(["--no-extensions"]);
            var trustedConfiguration = await ProjectRuntimeConfiguration.LoadAsync(project, agent, arguments,
                trust, interactiveTrust: false, TextReader.Null, TextWriter.Null, trustedOverride: true);
            using var trusted = await ProjectRuntimeContext.LoadAsync(trustedConfiguration, agent, arguments, null);
            Assert.True(trusted.Trusted);
            Assert.Empty(trusted.Extensions.LoadedBuiltins);
            Assert.True(trusted.Settings.BlockImages);
            Assert.Equal("true", trusted.Settings.TerminalTrueColor);
            Assert.False(trusted.Settings.SkillCommandsEnabled);
            Assert.Equal("export PISHARP_PREFIX=project", trusted.Settings.ShellCommandPrefix);
            var privateSkillResource = Assert.Single(trusted.Resources.Skills, skill => skill.Name == "private-guide");
            Assert.Equal("auto", privateSkillResource.SourceInfo.Source);
            Assert.Equal("project", privateSkillResource.SourceInfo.Scope);
            Assert.Equal(Path.Combine(project, ".pi"), privateSkillResource.SourceInfo.BaseDir);
            Assert.Equal(Path.GetFullPath(Path.Combine(project, "custom-sessions")), trusted.Store.DirectoryPath);
            var configuredResource = Assert.Single(trusted.Resources.Skills, skill => skill.Name == "configured-first");
            Assert.Equal("local", configuredResource.SourceInfo.Source);
            Assert.Equal("project", configuredResource.SourceInfo.Scope);
            Assert.Contains(trusted.Resources.Prompts, prompt => prompt.Name == "configured-review");

            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "settings.json"), "{\"shellCommandPrefix\":42}");
            var untrustedConfiguration = await ProjectRuntimeConfiguration.LoadAsync(project, agent, arguments,
                trust, interactiveTrust: false, TextReader.Null, TextWriter.Null, trustedOverride: false);
            using var untrusted = await ProjectRuntimeContext.LoadAsync(untrustedConfiguration, agent, arguments, null);
            Assert.False(untrusted.Trusted);
            Assert.Null(untrusted.ProjectSettings);
            Assert.Null(untrusted.Settings.BlockImages);
            Assert.Null(untrusted.Settings.TerminalTrueColor);
            Assert.True(untrusted.Settings.SkillCommandsEnabled);
            Assert.Equal("export PISHARP_PREFIX=user", untrusted.Settings.ShellCommandPrefix);
            Assert.DoesNotContain(untrusted.Resources.Skills, skill => skill.Name == "private-guide");
            Assert.DoesNotContain(untrusted.Resources.Skills, skill => skill.Name == "configured-first");
            Assert.DoesNotContain(untrusted.Resources.Prompts, prompt => prompt.Name == "configured-review");
            Assert.Empty(untrusted.Extensions.Registration.ToolDefinitions);
            Assert.Empty(untrusted.Extensions.LoadedBuiltins);

            var explicitBuiltinArguments = CliArguments.Parse(["--no-extensions", "--extension", "builtin:tool-search"]);
            var explicitBuiltinConfiguration = await ProjectRuntimeConfiguration.LoadAsync(project, agent,
                explicitBuiltinArguments, trust, interactiveTrust: false, TextReader.Null, TextWriter.Null,
                trustedOverride: false);
            using (var explicitBuiltin = await ProjectRuntimeContext.LoadAsync(explicitBuiltinConfiguration,
                agent, explicitBuiltinArguments, null))
            {
                Assert.Equal("tool_search", Assert.Single(explicitBuiltin.Extensions.Registration.Tools).Name);
                Assert.Contains("tool-search", explicitBuiltin.Extensions.LoadedBuiltins);
                Assert.DoesNotContain("mcp", explicitBuiltin.Extensions.LoadedBuiltins);
                Assert.Equal("builtin:tool-search", explicitBuiltin.Extensions.Registration
                    .ToolSourceInfo["tool_search"].Path);
            }

            var explicitMcpArguments = CliArguments.Parse(["--no-extensions", "--extension", "builtin:mcp"]);
            var explicitMcpConfiguration = await ProjectRuntimeConfiguration.LoadAsync(project, agent,
                explicitMcpArguments, trust, interactiveTrust: false, TextReader.Null, TextWriter.Null,
                trustedOverride: false);
            using (var explicitMcp = await ProjectRuntimeContext.LoadAsync(explicitMcpConfiguration,
                agent, explicitMcpArguments, null))
            {
                Assert.Contains("mcp", explicitMcp.Extensions.LoadedBuiltins);
                Assert.Equal("builtin:mcp", explicitMcp.Extensions.Registration.CommandInfo["mcp"].SourceInfo.Path);
                Assert.Equal("No MCP servers configured.", await explicitMcp.Extensions.Registration.Commands["mcp"](
                    "", CancellationToken.None));
            }

            var secondSkill = Path.Combine(project, ".pi", "configured-skills-next", "next");
            Directory.CreateDirectory(secondSkill);
            await File.WriteAllTextAsync(Path.Combine(secondSkill, "SKILL.md"),
                "---\nname: configured-second\ndescription: A reloaded project guide\n---\nUse the reloaded guide.");
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "settings.json"),
                "{\"skills\":[\"configured-skills-next\"]}");
            var reloadedConfiguration = await ProjectRuntimeConfiguration.LoadAsync(project, agent, arguments,
                trust, interactiveTrust: false, TextReader.Null, TextWriter.Null, trustedOverride: true);
            using var reloaded = await ProjectRuntimeContext.LoadAsync(reloadedConfiguration, agent, arguments, null);
            Assert.Contains(reloaded.Resources.Skills, skill => skill.Name == "configured-second");
            Assert.DoesNotContain(reloaded.Resources.Skills, skill => skill.Name == "configured-first");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RecordingProjectTrustUi : IProjectTrustExtensionUi
    {
        public List<string> Calls { get; } = [];

        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"select:{title}:{string.Join('|', options)}");
            return Task.FromResult<string?>("Continue");
        }

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"confirm:{title}:{message}");
            return Task.FromResult(true);
        }

        public Task<string?> InputAsync(string title, string? placeholder = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"input:{title}:{placeholder}");
            return Task.FromResult<string?>("approved");
        }

        public void Notify(string message, string type = "info") => Calls.Add($"notify:{type}:{message}");
    }

    private static async Task WriteResourcesAsync(string project, string phase, string skillName,
        string promptName, string themeName)
    {
        var root = Path.Combine(project, ".pi", "extension-resources", phase);
        var skillDirectory = Path.Combine(root, "skills");
        var promptDirectory = Path.Combine(root, "prompts");
        var themeDirectory = Path.Combine(root, "themes");
        Directory.CreateDirectory(skillDirectory);
        Directory.CreateDirectory(promptDirectory);
        Directory.CreateDirectory(themeDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"),
            $"---\nname: {skillName}\ndescription: Extension supplied skill\n---\nUse {skillName}.");
        await File.WriteAllTextAsync(Path.Combine(promptDirectory, promptName + ".md"),
            $"Review {promptName}.");
        using var resource = typeof(TerminalTheme).Assembly.GetManifestResourceStream("PiSharp.Cli.Tui.Themes.dark.json");
        Assert.NotNull(resource);
        using var reader = new StreamReader(resource!);
        var theme = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        theme["name"] = themeName;
        theme["colors"]!["mdHeading"] = phase == "startup" ? "#123456" : "#654321";
        await File.WriteAllTextAsync(Path.Combine(themeDirectory, themeName + ".json"), theme.ToJsonString());
    }

    private static void AssertTheme(ExtensionResourceDiscovery discovery, string project, string name,
        string headingColor)
    {
        var catalog = new TerminalThemeCatalog(project, project,
            environment: _ => null, trueColorOverride: true, discoverThemes: false,
            explicitThemePaths: discovery.ThemePaths.Select(path => path.Path).ToArray(),
            explicitThemeBaseDirectory: project);
        Assert.Contains(name, catalog.GetAvailableNames());
        Assert.Equal("\u001b[38;2;" + string.Join(';', Convert.FromHexString(headingColor[1..]).Select(value => value.ToString())) + "m",
            catalog.Load(name).Fg("mdHeading"));
    }
}
