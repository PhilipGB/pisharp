using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Sessions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class NativeExtensionCatalogTests
{
    [Fact]
    public async Task ProjectNativePackageIsInspectedButNotLoadedBeforeTrust()
    {
        using var temporary = new TemporaryDirectory();
        var workingDirectory = Path.Combine(temporary.Path, "project");
        var agentDirectory = Path.Combine(temporary.Path, "agent");
        var projectSettingsDirectory = Path.Combine(workingDirectory, ".pi");
        Directory.CreateDirectory(projectSettingsDirectory);
        Directory.CreateDirectory(agentDirectory);
        var packageDirectory = Path.Combine(temporary.Path, "packages");
        Directory.CreateDirectory(packageDirectory);
        var package = CreatePackage("1.0.0", "NativeExtensionFixture", "PiSharp.TestExtensions.NativeFixture",
            Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "NativeExtensionFixture", "bin", BuildConfiguration(), "net10.0"),
            packageDirectory);
        var installed = await new NativeExtensionPackageStore(agentDirectory)
            .InstallAsync(workingDirectory, package, "project");
        var source = Path.GetRelativePath(projectSettingsDirectory, installed.Source);
        await File.WriteAllTextAsync(Path.Combine(projectSettingsDirectory, "settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                shellCommandPrefix = 42,
                nativeExtensions = new[] { new { id = installed.Id, version = installed.Version, source } }
            }));

        var arguments = CliArguments.Parse([]);
        var trust = new ProjectTrust(agentDirectory);
        using (var untrustedConfiguration = await ProjectRuntimeConfiguration.LoadAsync(workingDirectory,
                   agentDirectory, arguments, trust, interactiveTrust: false, TextReader.Null, TextWriter.Null,
                   trustedOverride: false))
        {
            Assert.False(untrustedConfiguration.Trusted);
            Assert.Null(untrustedConfiguration.ProjectSettings);
            Assert.Equal("project", Assert.Single(untrustedConfiguration.NativeExtensionPackageRoots).Scope);
            using var untrusted = await ProjectRuntimeContext.LoadAsync(untrustedConfiguration, agentDirectory,
                arguments, null);
            Assert.DoesNotContain("native-fixture", untrusted.Extensions.Registration.Commands.Keys);
            Assert.Empty(untrusted.Extensions.Registration.ExtensionFlags);
            Assert.Contains(untrusted.Extensions.Warnings, warning => warning.Warning.Contains(
                "not loaded because this project is not trusted", StringComparison.Ordinal));
            var discovery = await untrusted.Extensions.DiscoverResourcesAsync(workingDirectory,
                ExtensionResourceDiscoveryReason.Startup);
            Assert.Empty(discovery.SkillPaths);
            Assert.Empty(discovery.PromptPaths);
            Assert.Empty(discovery.ThemePaths);
        }

        await File.WriteAllTextAsync(Path.Combine(projectSettingsDirectory, "settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                nativeExtensions = new[] { new { id = installed.Id, version = installed.Version, source } }
            }));
        using var trustedConfiguration = await ProjectRuntimeConfiguration.LoadAsync(workingDirectory,
            agentDirectory, arguments, trust, interactiveTrust: false, TextReader.Null, TextWriter.Null,
            trustedOverride: true);
        using var trusted = await ProjectRuntimeContext.LoadAsync(trustedConfiguration, agentDirectory, arguments, null);
        Assert.True(trusted.Trusted);
        Assert.Contains("native-fixture", trusted.Extensions.Registration.Commands.Keys);
    }

    [Fact]
    public async Task CatalogReloadReconcilesAndUnloadsReplacedNativePackageContext()
    {
        using var temporary = new TemporaryDirectory();
        var workingDirectory = Path.Combine(temporary.Path, "project");
        var agentDirectory = Path.Combine(temporary.Path, "agent");
        var packages = Path.Combine(temporary.Path, "packages");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(packages);

        var fixtureRoot = Path.Combine(FindRepositoryRoot(), "tests", "fixtures");
        var installedV1 = await new NativeExtensionPackageStore(agentDirectory).InstallAsync(workingDirectory,
            CreatePackage("1.0.0", "NativeExtensionFixture", "PiSharp.TestExtensions.NativeFixture",
                Path.Combine(fixtureRoot, "NativeExtensionFixture", "bin", BuildConfiguration(), "net10.0"), packages),
            "user");
        var installedV2 = await new NativeExtensionPackageStore(agentDirectory).InstallAsync(workingDirectory,
            CreatePackage("2.0.0", "NativeExtensionFixtureV2", "PiSharp.TestExtensions.NativeFixture",
                Path.Combine(fixtureRoot, "NativeExtensionFixtureV2", "bin", BuildConfiguration(), "net10.0"), packages),
            "user");

        var store = new NativeExtensionPackageStore(agentDirectory);
        var v1Roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV1]);
        var v2Roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV2]);
        var (catalog, v1LoadContext) = LoadAndReplaceNativePackageCatalog(agentDirectory, workingDirectory,
            v1Roots, v2Roots);
        try
        {
            Assert.Equal(["native-fixture-v2-enabled", "native-fixture-v2-label"],
                catalog.Registration.ExtensionFlags.Select(flag => flag.Name).Order(StringComparer.Ordinal));
            AssertContextUnloaded(v1LoadContext);
            AssertReplacementAgentStartHandler(catalog, v2Roots[0].Path);
            var (removedCatalog, v2LoadContext) = LoadAndRemoveNativePackageCatalog(agentDirectory,
                workingDirectory, catalog);
            catalog = removedCatalog;
            Assert.Empty(catalog.Registration.ExtensionFlags);
            Assert.Empty(catalog.Registration.ExtensionShortcuts);
            Assert.Empty(catalog.Registration.SessionStartHandlers);
            Assert.Empty(catalog.Registration.SessionShutdownHandlers);
            Assert.Empty(catalog.Registration.AgentStartHandlers);
            Assert.Empty(catalog.Registration.SessionBeforeSwitchHandlers);
            Assert.Empty(catalog.Registration.SessionBeforeForkHandlers);
            Assert.Empty(catalog.Registration.SessionBeforeTreeHandlers);
            Assert.Empty(catalog.Registration.SessionTreeHandlers);
            Assert.Empty(catalog.Registration.SessionBeforeCompactHandlers);
            Assert.Empty(catalog.Registration.InputHandlers);
            Assert.Empty(catalog.Registration.RegisteredUserBashHandlers);
            AssertContextUnloaded(v2LoadContext);
        }
        finally { catalog.Dispose(); }
    }

    [Fact]
    public async Task ReloadDuringAnActiveNativeCallbackDefersCollectionUntilTheCallbackSettles()
    {
        using var temporary = new TemporaryDirectory();
        var workingDirectory = Path.Combine(temporary.Path, "project");
        var agentDirectory = Path.Combine(temporary.Path, "agent");
        var packages = Path.Combine(temporary.Path, "packages");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(packages);

        var fixtureRoot = Path.Combine(FindRepositoryRoot(), "tests", "fixtures");
        var store = new NativeExtensionPackageStore(agentDirectory);
        var installedV1 = await store.InstallAsync(workingDirectory,
            CreatePackage("1.0.0", "NativeExtensionFixture", "PiSharp.TestExtensions.NativeFixture",
                Path.Combine(fixtureRoot, "NativeExtensionFixture", "bin", BuildConfiguration(), "net10.0"), packages),
            "user");
        var installedV2 = await store.InstallAsync(workingDirectory,
            CreatePackage("2.0.0", "NativeExtensionFixtureV2", "PiSharp.TestExtensions.NativeFixture",
                Path.Combine(fixtureRoot, "NativeExtensionFixtureV2", "bin", BuildConfiguration(), "net10.0"), packages),
            "user");
        var v1Roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV1]);
        var v2Roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV2]);

        await ReloadDuringActiveNativeCallbackAsync(agentDirectory, workingDirectory, v1Roots, v2Roots);
    }

    [Fact]
    public async Task PublicResourceDiscoveryLoadsOnStartupAndReplacesResourcesOnReloadAndRemoval()
    {
        using var temporary = new TemporaryDirectory();
        var workingDirectory = Path.Combine(temporary.Path, "project");
        var agentDirectory = Path.Combine(temporary.Path, "agent");
        var packages = Path.Combine(temporary.Path, "packages");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(packages);

        var fixtureRoot = Path.Combine(FindRepositoryRoot(), "tests", "fixtures");
        var store = new NativeExtensionPackageStore(agentDirectory);
        var installedV1 = await store.InstallAsync(workingDirectory,
            CreatePackage("1.0.0", "NativeExtensionFixture", "PiSharp.TestExtensions.NativeFixture",
                Path.Combine(fixtureRoot, "NativeExtensionFixture", "bin", BuildConfiguration(), "net10.0"), packages),
            "user");
        var installedV2 = await store.InstallAsync(workingDirectory,
            CreatePackage("2.0.0", "NativeExtensionFixtureV2", "PiSharp.TestExtensions.NativeFixture",
                Path.Combine(fixtureRoot, "NativeExtensionFixtureV2", "bin", BuildConfiguration(), "net10.0"), packages),
            "user");

        var v1Roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV1]);
        var catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: v1Roots);
        try
        {
            var startup = await catalog.DiscoverResourcesAsync(workingDirectory, ExtensionResourceDiscoveryReason.Startup);
            var startupResources = await LoadExtensionResourcesAsync(workingDirectory, agentDirectory, startup);
            Assert.Equal("native-fixture-v1-startup", Assert.Single(startupResources.Skills).Name);
            Assert.Equal("native-fixture-v1-startup", Assert.Single(startupResources.Prompts).Name);
            var startupSkill = Assert.Single(startup.SkillPaths);
            var v1Source = Assert.Single(catalog.LoadedExtensions);
            Assert.Equal("extension:" + Path.GetFileNameWithoutExtension(v1Source.Path), startupSkill.SourceInfo.Source);
            Assert.Equal("temporary", startupSkill.SourceInfo.Scope);
            Assert.Equal("top-level", startupSkill.SourceInfo.Origin);
            Assert.Equal(Path.GetDirectoryName(v1Source.Path), startupSkill.SourceInfo.BaseDir);
            Assert.Single(startup.ThemePaths);

            var v1Reload = await catalog.DiscoverResourcesAsync(workingDirectory, ExtensionResourceDiscoveryReason.Reload);
            var reloadedResources = await LoadExtensionResourcesAsync(workingDirectory, agentDirectory, v1Reload);
            Assert.Equal("native-fixture-v1-reload", Assert.Single(reloadedResources.Skills).Name);
            Assert.Equal("native-fixture-v1-reload", Assert.Single(reloadedResources.Prompts).Name);
            Assert.DoesNotContain(reloadedResources.Skills, skill => skill.Name == "native-fixture-v1-startup");
            Assert.DoesNotContain(reloadedResources.Prompts, prompt => prompt.Name == "native-fixture-v1-startup");

            var v2Roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV2]);
            catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
                preloaded: catalog, nativeExtensionPackageRoots: v2Roots);
            var v2Reload = await catalog.DiscoverResourcesAsync(workingDirectory, ExtensionResourceDiscoveryReason.Reload);
            var v2Resources = await LoadExtensionResourcesAsync(workingDirectory, agentDirectory, v2Reload);
            Assert.Equal("native-fixture-v2-reload", Assert.Single(v2Resources.Skills).Name);
            Assert.Equal("native-fixture-v2-reload", Assert.Single(v2Resources.Prompts).Name);
            Assert.DoesNotContain(v2Resources.Skills,
                skill => skill.Name.StartsWith("native-fixture-v1-", StringComparison.Ordinal));
            Assert.DoesNotContain(v2Resources.Prompts,
                prompt => prompt.Name.StartsWith("native-fixture-v1-", StringComparison.Ordinal));
            var v2Source = Assert.Single(catalog.LoadedExtensions);
            Assert.Equal("extension:" + Path.GetFileNameWithoutExtension(v2Source.Path),
                Assert.Single(v2Reload.SkillPaths).SourceInfo.Source);

            catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
                preloaded: catalog, nativeExtensionPackageRoots: []);
            var afterRemoval = await catalog.DiscoverResourcesAsync(workingDirectory, ExtensionResourceDiscoveryReason.Reload);
            Assert.Empty(afterRemoval.SkillPaths);
            Assert.Empty(afterRemoval.PromptPaths);
            Assert.Empty(afterRemoval.ThemePaths);
            var removedResources = await LoadExtensionResourcesAsync(workingDirectory, agentDirectory, afterRemoval);
            Assert.Empty(removedResources.Skills);
            Assert.Empty(removedResources.Prompts);
            Assert.DoesNotContain("native-fixture", catalog.Registration.Commands.Keys);
            Assert.Empty(catalog.Registration.ContextCommands);
            Assert.Empty(catalog.Registration.ToolDefinitions);
            Assert.Empty(catalog.Registration.BeforeAgentStartHandlers);
            Assert.Empty(catalog.Registration.ContextTransforms);
            Assert.Empty(catalog.Registration.ToolCallHooks);
            Assert.Empty(catalog.Registration.ToolResultHooks);
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [Fact]
    public async Task PublicNativeFixtureCallbacksRunThroughLoadedPackagesAndV1CallbacksDisappearOnReload()
    {
        using var temporary = new TemporaryDirectory();
        var workingDirectory = Path.Combine(temporary.Path, "project");
        var agentDirectory = Path.Combine(temporary.Path, "agent");
        var packages = Path.Combine(temporary.Path, "packages");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(packages);

        var fixtureRoot = Path.Combine(FindRepositoryRoot(), "tests", "fixtures");
        var store = new NativeExtensionPackageStore(agentDirectory);
        var installedV1 = await store.InstallAsync(workingDirectory,
            CreatePackage("1.0.0", "NativeExtensionFixture", "PiSharp.TestExtensions.NativeFixture",
                Path.Combine(fixtureRoot, "NativeExtensionFixture", "bin", BuildConfiguration(), "net10.0"), packages),
            "user");
        var installedV2 = await store.InstallAsync(workingDirectory,
            CreatePackage("2.0.0", "NativeExtensionFixtureV2", "PiSharp.TestExtensions.NativeFixture",
                Path.Combine(fixtureRoot, "NativeExtensionFixtureV2", "bin", BuildConfiguration(), "net10.0"), packages),
            "user");

        var v1Roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV1]);
        var catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: v1Roots);
        try
        {
            var sentMessages = new List<string>();
            var commandContext = new PiSharp.Extensions.Abstractions.PiSharpExtensionCommandContext(
                workingDirectory, isIdle: true, isProjectTrusted: true, hasPendingMessages: false,
                (text, _) =>
                {
                    sentMessages.Add("user:" + text);
                    return Task.CompletedTask;
                },
                (message, triggerTurn, deliveryMode, _) =>
                {
                    sentMessages.Add($"custom:{message.CustomType}:{message.Content}:{triggerTurn}:{deliveryMode}");
                    return Task.CompletedTask;
                });
            var sendCommand = catalog.Registration.ContextCommands["native-fixture-send"];
            Assert.Equal("v1 command sent user", await sendCommand("user hello", commandContext,
                CancellationToken.None));
            Assert.Equal("v1 command sent custom", await sendCommand("custom background work", commandContext,
                CancellationToken.None));
            Assert.Equal(
                ["user:hello", "custom:native-fixture-v1-command:background work:True:Steer"], sentMessages);

            var v1Client = new NativeFixtureHookChatClient();
            var v1Conversation = new ConversationSession(workingDirectory, "fixture", null);
            var v1Agent = CreateFixtureAgent(v1Client, workingDirectory, catalog.Registration);
            var v1Run = await ConversationRun.OpenAsync(v1Agent, v1Conversation);
            var v1Switch = await v1Run.DispatchSessionBeforeSwitchAsync(
                PiSharp.Extensions.Abstractions.PiSharpExtensionSessionSwitchReason.Resume,
                "native-fixture-v1", null, CancellationToken.None);
            Assert.True(v1Switch?.Cancel);
            await foreach (var _ in v1Run.RunEventsAsync("initial user turn")) { }

            Assert.Equal(2, v1Client.Requests.Count);
            Assert.Equal(
                ["native-fixture-v1-result:v1 tool / private dependency 1.0.0.0: native-fixture-v1-agent-loop"],
                v1Client.NativeToolResults);
            Assert.All(v1Client.Instructions.Take(2), instructions => Assert.Contains(
                "native-fixture-v1-before-user", instructions, StringComparison.Ordinal));
            Assert.Single(v1Run.Conversation.Tree.ActivePath(), node => node.Type == "custom_message" &&
                node.Payload.GetProperty("piOriginalEntry").GetProperty("customType").GetString() ==
                "native-fixture-v1-injected");

            await foreach (var _ in v1Run.RunCustomMessageEventsAsync(
                               new PiSharp.Extensions.Abstractions.PiSharpCustomMessage(
                                   "native-fixture-v1-external", "custom start turn"), sourcePath: null)) { }

            Assert.Equal(4, v1Client.Requests.Count);
            Assert.All(v1Client.Instructions.Skip(2), instructions => Assert.Contains(
                "native-fixture-v1-before-custom:native-fixture-v1-external", instructions,
                StringComparison.Ordinal));
            Assert.Single(v1Run.Conversation.Tree.ActivePath(), node => node.Type == "custom_message" &&
                node.Payload.GetProperty("piOriginalEntry").GetProperty("customType").GetString() ==
                "native-fixture-v1-injected");
            Assert.Equal(
                [
                    "native-fixture-v1-result:v1 tool / private dependency 1.0.0.0: native-fixture-v1-agent-loop",
                    "native-fixture-v1-result:v1 tool / private dependency 1.0.0.0: native-fixture-v1-custom-loop"
                ], v1Client.NativeToolResults);
            Assert.All(v1Client.Requests, request =>
            {
                var lastUserMessage = request.Last(message => message.Role == ChatRole.User);
                Assert.Contains("[native-fixture-v1-transform]", lastUserMessage.Text, StringComparison.Ordinal);
                Assert.Equal(1, lastUserMessage.Text.Split("[native-fixture-v1-transform]", StringSplitOptions.None).Length - 1);
            });

            var v2Roots = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV2]);
            catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
                preloaded: catalog, nativeExtensionPackageRoots: v2Roots);
            Assert.Contains("native-fixture-send", catalog.Registration.ContextCommands.Keys);
            Assert.Contains("native-fixture-state-set", catalog.Registration.ContextCommands.Keys);
            Assert.Contains("native-fixture-state-show", catalog.Registration.ContextCommands.Keys);
            Assert.Contains("native-fixture-mode", catalog.Registration.ContextCommands.Keys);
            Assert.Contains("native-fixture-ui", catalog.Registration.ContextCommands.Keys);
            Assert.Contains("native-fixture-controls", catalog.Registration.ContextCommands.Keys);
            Assert.DoesNotContain(v1Roots[0].Path,
                catalog.Registration.CommandInfo.Values.Select(info => info.SourceInfo.Path));
            Assert.DoesNotContain(v1Roots[0].Path,
                catalog.Registration.BeforeAgentStartHandlers.Select(handler => handler.SourceInfo.Path));
            var reloadedSendCommand = catalog.Registration.ContextCommands["native-fixture-send"];
            Assert.Equal("v2 command sent custom", await reloadedSendCommand("custom after reload", commandContext,
                CancellationToken.None));
            Assert.Equal("custom:native-fixture-v2-command:after reload:True:Steer", sentMessages[^1]);

            var v2Client = new NativeFixtureHookChatClient();
            var v2Run = await ConversationRun.OpenAsync(CreateFixtureAgent(v2Client, workingDirectory,
                    catalog.Registration), new ConversationSession(workingDirectory, "fixture", null));
            var staleV1Switch = await v2Run.DispatchSessionBeforeSwitchAsync(
                PiSharp.Extensions.Abstractions.PiSharpExtensionSessionSwitchReason.Resume,
                "native-fixture-v1", null, CancellationToken.None);
            Assert.Null(staleV1Switch);
            var v2Switch = await v2Run.DispatchSessionBeforeSwitchAsync(
                PiSharp.Extensions.Abstractions.PiSharpExtensionSessionSwitchReason.Resume,
                "native-fixture-v2", null, CancellationToken.None);
            Assert.True(v2Switch?.Cancel);
            await foreach (var _ in v2Run.RunEventsAsync("reloaded user turn")) { }

            Assert.Equal(2, v2Client.Requests.Count);
            Assert.Equal(
                ["native-fixture-v2-result:v2 tool / private dependency 2.0.0.0: native-fixture-v2-agent-loop"],
                v2Client.NativeToolResults);
            Assert.All(v2Client.Instructions, instructions =>
            {
                Assert.Contains("native-fixture-v2-before-user", instructions, StringComparison.Ordinal);
                Assert.DoesNotContain("native-fixture-v1-before-user", instructions, StringComparison.Ordinal);
            });
            Assert.All(v2Client.NativeToolResults, result => Assert.StartsWith(
                "native-fixture-v2-result:v2 tool / private dependency 2.0.0.0: native-fixture-v2-", result));
            Assert.All(v2Client.Requests, request => Assert.Contains("[native-fixture-v2-transform]",
                request.Last(message => message.Role == ChatRole.User).Text, StringComparison.Ordinal));
        }
        finally
        {
            catalog.Dispose();
        }
    }

    [Fact]
    public async Task PackedExtensionsConfigureToolsCommandsAndLifecycleAndReloadByVersion()
    {
        using var temporary = new TemporaryDirectory();
        var workingDirectory = Path.Combine(temporary.Path, "project");
        var agentDirectory = Path.Combine(temporary.Path, "agent");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(agentDirectory);
        var packages = Path.Combine(temporary.Path, "packages");
        Directory.CreateDirectory(packages);

        var versionOne = CreatePackage("1.0.0", "NativeExtensionFixture", "PiSharp.TestExtensions.NativeFixture",
            Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "NativeExtensionFixture", "bin", BuildConfiguration(), "net10.0"),
            packages);
        var versionTwo = CreatePackage("2.0.0", "NativeExtensionFixtureV2", "PiSharp.TestExtensions.NativeFixture",
            Path.Combine(FindRepositoryRoot(), "tests", "fixtures", "NativeExtensionFixtureV2", "bin", BuildConfiguration(), "net10.0"),
            packages);
        var store = new NativeExtensionPackageStore(agentDirectory);
        var installedV1 = await store.InstallAsync(workingDirectory, versionOne, "user");
        var installedV2 = await store.InstallAsync(workingDirectory, versionTwo, "user");

        var bothRoots = await store.ResolveAsync(workingDirectory, trusted: true,
            userSources: [installedV1, installedV2]);
        using (var both = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
                   nativeExtensionPackageRoots: bothRoots))
        {
            Assert.Equal("v1 command / private dependency 1.0.0.0: hello",
                await both.Registration.Commands["native-fixture:1"]("hello", CancellationToken.None));
            Assert.Equal("v2 command / private dependency 2.0.0.0: hello",
                await both.Registration.Commands["native-fixture:2"]("hello", CancellationToken.None));
            var tool = Assert.Single(both.Registration.ToolDefinitions,
                definition => definition.Function.Name == "native_fixture_tool");
            var client = new NativeExtensionToolLoopChatClient();
            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            var agent = new PiAgent(client, new CodingTools(workingDirectory), selectedTools: ["native_fixture_tool"],
                noTools: true, noBuiltinTools: true, extensionToolRegistrations: [tool], systemPrompt: "base system prompt",
                beforeAgentStartHandlers: both.Registration.BeforeAgentStartHandlers);
            var run = await ConversationRun.OpenAsync(agent, conversation);
            await foreach (var _ in run.RunEventsAsync("hello")) { }
            Assert.Equal(2, client.RequestedTools.Count);
            Assert.Contains("native_fixture_tool", client.RequestedTools[0]);
            Assert.Equal("v1 tool / private dependency 1.0.0.0: agent-loop", client.NativeToolResult);
            Assert.Contains("native extension result", client.FinalResponse!, StringComparison.Ordinal);
            Assert.Contains(conversation.ActiveMessages().SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>(), result => result.CallId == "native-tool-call" &&
                    result.Result?.ToString() == "v1 tool / private dependency 1.0.0.0: agent-loop");
            Assert.NotEmpty(client.Instructions);
            var instructions = client.Instructions[0];
            Assert.Contains("v1 extension / private dependency 1.0.0.0", instructions, StringComparison.Ordinal);
            Assert.Contains("v2 extension / private dependency 2.0.0.0", instructions, StringComparison.Ordinal);
            Assert.Contains(workingDirectory, instructions, StringComparison.Ordinal);
        }

        var projectInstall = await store.InstallAsync(workingDirectory, versionOne, "project");
        var untrustedRoots = await store.ResolveAsync(workingDirectory, trusted: false,
            projectSources: [projectInstall]);
        using (var untrusted = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: false,
                   nativeExtensionPackageRoots: untrustedRoots))
        {
            Assert.Empty(untrusted.Registration.Commands);
            Assert.Empty(untrusted.LoadedExtensions);
            Assert.Contains(untrusted.Warnings, warning => warning.Warning.Contains(
                "not loaded because this project is not trusted", StringComparison.Ordinal));
        }
        using (var trusted = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
                   nativeExtensionPackageRoots: untrustedRoots))
            Assert.Contains("native-fixture", trusted.Registration.Commands.Keys);

        var activeV1 = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV1]);
        var reloaded = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: activeV1);
        try
        {
            Assert.StartsWith("v1 command", InvokeNativeFixtureCommand(reloaded, "before"));
            var activeV2 = await store.ResolveAsync(workingDirectory, trusted: true, userSources: [installedV2]);
            reloaded = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
                preloaded: reloaded, nativeExtensionPackageRoots: activeV2);
            var v2Result = InvokeNativeFixtureCommand(reloaded, "after");
            Assert.StartsWith("v2 command", v2Result);
            Assert.Contains("2.0.0.0", v2Result);
        }
        finally { reloaded.Dispose(); }

        var broken = await CreateBrokenEntryPackageAsync(versionOne, Path.Combine(packages, "broken-entry.nupkg"));
        var brokenInstall = await store.InstallAsync(workingDirectory, broken, "user");
        var healthyAndBrokenRoots = await store.ResolveAsync(workingDirectory, trusted: true,
            userSources: [installedV1, brokenInstall]);
        using var isolated = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: healthyAndBrokenRoots);
        Assert.Contains(isolated.Warnings, warning => warning.Warning.Contains(
            "does not contain declared entry type", StringComparison.Ordinal));
        Assert.Contains("native_fixture_tool", isolated.Registration.ToolSourceInfo.Keys);

        var ordinaryClient = new NativeExtensionChatClient();
        var ordinaryAgent = new PiAgent(ordinaryClient, new CodingTools(workingDirectory), systemPrompt: "ordinary prompt",
            extensionToolRegistrations: isolated.Registration.ToolDefinitions,
            beforeAgentStartHandlers: isolated.Registration.BeforeAgentStartHandlers);
        var ordinaryRun = await ConversationRun.OpenAsync(ordinaryAgent,
            new ConversationSession(workingDirectory, "ordinary", null));
        await foreach (var _ in ordinaryRun.RunEventsAsync("ordinary conversation")) { }
        Assert.Contains("ordinary prompt", Assert.Single(ordinaryClient.Instructions), StringComparison.Ordinal);
    }

    private static PiAgent CreateFixtureAgent(NativeFixtureHookChatClient client, string workingDirectory,
        ExtensionRegistration registration)
    {
        var tool = Assert.Single(registration.ToolDefinitions,
            definition => definition.Function.Name == "native_fixture_tool");
        return new PiAgent(client, new CodingTools(workingDirectory), selectedTools: ["native_fixture_tool"],
            noTools: true, noBuiltinTools: true, extensionToolRegistrations: [tool],
            systemPrompt: "native fixture test prompt",
            liveExtensionRegistration: registration,
            beforeAgentStartHandlers: registration.BeforeAgentStartHandlers,
            extensionContextTransforms: registration.ContextTransforms,
            extensionToolCallHooks: registration.ToolCallHooks,
            extensionToolResultHooks: registration.ToolResultHooks);
    }

    private static string CreatePackage(string version, string assemblyName, string packageId,
        string fixtureOutput, string outputDirectory)
    {
        var package = Path.Combine(outputDirectory, $"{packageId}.{version}.nupkg");
        var assemblyFile = assemblyName + ".dll";
        var entryAssemblyPath = "lib/net10.0/" + assemblyFile;
        using var output = File.Create(package);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        var manifest = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = packageId,
            version,
            apiVersion = 1,
            extensions = new[]
            {
                new { assembly = entryAssemblyPath, type = "PiSharp.TestExtensions.NativeFixture.ExampleExtension" }
            }
        });
        WriteArchiveText(archive, "pisharp-extension.json", manifest);
        WriteArchiveText(archive, packageId + ".nuspec",
            $"<?xml version=\"1.0\"?><package><metadata><id>{packageId}</id><version>{version}</version>" +
            "<authors>PiSharp tests</authors><description>Native extension fixture.</description></metadata></package>");
        AddArchiveFile(archive, entryAssemblyPath, Path.Combine(fixtureOutput, assemblyFile));
        AddArchiveFile(archive, "lib/net10.0/" + assemblyName + ".deps.json",
            Path.Combine(fixtureOutput, assemblyName + ".deps.json"));
        AddArchiveFile(archive, "lib/net10.0/NativeExtensionPrivateDependency.dll",
            Path.Combine(fixtureOutput, "NativeExtensionPrivateDependency.dll"));
        AddArchiveDirectoryFiles(archive, "lib/net10.0/Resources", Path.Combine(fixtureOutput, "Resources"));
        return package;
    }

    private static void AddArchiveDirectoryFiles(ZipArchive archive, string archiveDirectory, string sourceDirectory)
    {
        Assert.True(Directory.Exists(sourceDirectory), $"Native extension fixture resources are missing: {sourceDirectory}");
        foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
            AddArchiveFile(archive, archiveDirectory + "/" + relative, path);
        }
    }

    private static Task<ResourceCatalog> LoadExtensionResourcesAsync(string workingDirectory, string agentDirectory,
        ExtensionResourceDiscovery discovery) => ResourceCatalog.LoadAsync(workingDirectory, agentDirectory, trusted: false,
        discoverSkills: false, discoverPrompts: false, extensionResources: discovery);

    private static void WriteArchiveText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void AddArchiveFile(ZipArchive archive, string name, string path)
    {
        Assert.True(File.Exists(path), $"Native extension fixture build output is missing: {path}");
        var entry = archive.CreateEntry(name);
        using var target = entry.Open();
        using var source = File.OpenRead(path);
        source.CopyTo(target);
    }

    private static string BuildConfiguration() => new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string InvokeNativeFixtureCommand(ExtensionCatalog catalog, string value) =>
        catalog.Registration.Commands["native-fixture"](value, CancellationToken.None).GetAwaiter().GetResult();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ExtensionCatalog Catalog, WeakReference PreviousLoadContext) LoadAndReplaceNativePackageCatalog(
        string agentDirectory, string workingDirectory, IReadOnlyList<NativeExtensionPackageRoot> v1Roots,
        IReadOnlyList<NativeExtensionPackageRoot> v2Roots)
    {
        var catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: v1Roots);
        var discovery = catalog.DiscoverResourcesAsync(workingDirectory, ExtensionResourceDiscoveryReason.Startup)
            .GetAwaiter().GetResult();
        Assert.Single(discovery.SkillPaths);
        var previousLoadContext = Assert.Single(catalog.NativePackageLoadContextReferences);

        var reloaded = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            preloaded: catalog, nativeExtensionPackageRoots: v2Roots);
        Assert.True(reloaded.Registration.Commands.ContainsKey("native-fixture"),
            string.Join(Environment.NewLine, reloaded.LoadErrors.Select(error => error.Error)));
        Assert.Equal(v2Roots[0].Path, reloaded.Registration.ToolSourceInfo["native_fixture_tool"].Path);
        Assert.DoesNotContain(v1Roots[0].Path,
            reloaded.Registration.CommandInfo.Values.Select(info => info.SourceInfo.Path));
        Assert.DoesNotContain(v1Roots[0].Path,
            reloaded.Registration.BeforeAgentStartHandlers.Select(handler => handler.SourceInfo.Path));
        Assert.DoesNotContain(v1Roots[0].Path,
            reloaded.Registration.AgentStartHandlers.Select(handler => handler.SourceInfo.Path));
        Assert.DoesNotContain(v1Roots[0].Path,
            reloaded.Registration.InputHandlers.Select(handler => handler.SourceInfo.Path));
        Assert.DoesNotContain(v1Roots[0].Path,
            reloaded.Registration.RegisteredUserBashHandlers.Select(handler => handler.SourceInfo.Path));
        Assert.DoesNotContain(v1Roots[0].Path, reloaded.LoadedExtensions.Select(extension => extension.Path));
        Assert.Single(reloaded.NativePackageLoadContextReferences);
        Assert.Equal(v2Roots[0].Path, Assert.Single(reloaded.Registration.InputHandlers).SourceInfo.Path);
        Assert.Equal(v2Roots[0].Path,
            Assert.Single(reloaded.Registration.RegisteredUserBashHandlers).SourceInfo.Path);
        return (reloaded, previousLoadContext);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ExtensionCatalog Catalog, WeakReference PreviousLoadContext) LoadAndRemoveNativePackageCatalog(
        string agentDirectory, string workingDirectory, ExtensionCatalog activeCatalog)
    {
        var previousLoadContext = Assert.Single(activeCatalog.NativePackageLoadContextReferences);
        var removed = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            preloaded: activeCatalog, nativeExtensionPackageRoots: []);
        Assert.Empty(removed.Registration.ExtensionShortcuts);
        Assert.Empty(removed.Registration.SessionStartHandlers);
        Assert.Empty(removed.Registration.SessionShutdownHandlers);
        Assert.Empty(removed.Registration.AgentStartHandlers);
        Assert.Empty(removed.Registration.InputHandlers);
        Assert.Empty(removed.Registration.RegisteredUserBashHandlers);
        return (removed, previousLoadContext);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ReloadDuringActiveNativeCallbackAsync(string agentDirectory,
        string workingDirectory, IReadOnlyList<NativeExtensionPackageRoot> v1Roots,
        IReadOnlyList<NativeExtensionPackageRoot> v2Roots)
    {
        var catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
            nativeExtensionPackageRoots: v1Roots);
        var previousContext = Assert.Single(catalog.NativePackageLoadContextReferences);
        var gatePath = Path.Combine(workingDirectory, "callback-gate");
        var run = await ConversationRun.OpenAsync(CreateFixtureAgent(new NativeFixtureHookChatClient(), workingDirectory,
                catalog.Registration), new ConversationSession(workingDirectory, "fixture", null));
        using var cancellation = new CancellationTokenSource();
        var execution = Task.Run(async () =>
        {
            await foreach (var _ in run.RunEventsAsync("__native_fixture_hold_callback:" + gatePath,
                               cancellation.Token)) { }
        });
        try
        {
            await WaitForFileAsync(gatePath + ".started");
            catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory, projectTrusted: true,
                preloaded: catalog, nativeExtensionPackageRoots: v2Roots);
            Assert.True(run.IsActive);
            Assert.True(previousContext.IsAlive,
                "The extension load context must remain alive while its callback is executing.");

            await File.WriteAllTextAsync(gatePath + ".release", "release");
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsActive);
            run = null!;
            execution = Task.CompletedTask;
            catalog.Dispose();
            catalog = null!;
        }
        finally
        {
            if (File.Exists(gatePath + ".started") && !File.Exists(gatePath + ".release"))
                await File.WriteAllTextAsync(gatePath + ".release", "release");
            if (!execution.IsCompleted)
            {
                cancellation.Cancel();
                try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
            }
            catalog?.Dispose();
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!File.Exists(path))
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"The extension callback did not create {path}.");
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertContextUnloaded(WeakReference context)
    {
        for (var attempt = 0; attempt < 20 && context.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(context.IsAlive, "The native extension load context remained reachable after catalog reload.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertReplacementAgentStartHandler(ExtensionCatalog catalog, string expectedSource)
    {
        var handler = Assert.Single(catalog.Registration.AgentStartHandlers);
        Assert.Equal(expectedSource, handler.SourceInfo.Path);
    }

    private static async Task<string> CreateBrokenEntryPackageAsync(string source, string destination)
    {
        await using var input = File.OpenRead(source);
        using var inputArchive = new ZipArchive(input, ZipArchiveMode.Read);
        await using var output = File.Create(destination);
        using var outputArchive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var entry in inputArchive.Entries)
        {
            var target = outputArchive.CreateEntry(entry.FullName);
            await using var targetStream = target.Open();
            if (entry.FullName == "pisharp-extension.json")
            {
                await using var sourceStream = entry.Open();
                using var document = await System.Text.Json.JsonDocument.ParseAsync(sourceStream);
                var manifest = new
                {
                    id = "Example.BrokenEntry",
                    version = "1.0.0",
                    apiVersion = 1,
                    extensions = new[]
                    {
                        new
                        {
                            assembly = document.RootElement.GetProperty("extensions")[0].GetProperty("assembly").GetString(),
                            type = "Example.MissingExtensionType"
                        }
                    }
                };
                await System.Text.Json.JsonSerializer.SerializeAsync(targetStream, manifest);
            }
            else
            {
                await using var sourceStream = entry.Open();
                await sourceStream.CopyToAsync(targetStream);
            }
        }
        return destination;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find PiSharp.slnx.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("pisharp-native-catalog-").FullName;
        internal string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class NativeExtensionToolLoopChatClient : IChatClient
    {
        private int _requests;
        public List<string> Instructions { get; } = [];
        public List<string[]> RequestedTools { get; } = [];
        public string? NativeToolResult { get; private set; }
        public string? FinalResponse { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Instructions.Add(options?.Instructions ?? "");
            RequestedTools.Add(options?.Tools?.OfType<AIFunction>().Select(tool => tool.Name).ToArray() ?? []);
            if (Interlocked.Increment(ref _requests) == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("native-tool-call", "native_fixture_tool",
                        new Dictionary<string, object?> { ["value"] = "agent-loop" })]);
            }
            else
            {
                NativeToolResult = messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>().Single(result => result.CallId == "native-tool-call")
                    .Result?.ToString();
                FinalResponse = "assistant received native extension result";
                yield return new ChatResponseUpdate(ChatRole.Assistant, FinalResponse);
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NativeFixtureHookChatClient : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];
        public List<string> Instructions { get; } = [];
        public List<string> NativeToolResults { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = Requests.Count;
            Requests.Add(messages.Select(message => message.Clone()).ToArray());
            Instructions.Add(options?.Instructions ?? "");
            var turn = request / 2 + 1;
            var callId = "native-fixture-tool-call-" + turn;
            if (request % 2 == 0)
            {
                var value = turn == 1 ? "agent-loop" : "custom-loop";
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent(callId, "native_fixture_tool",
                        new Dictionary<string, object?> { ["value"] = value })]);
            }
            else
            {
                var result = messages.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>().Single(content => content.CallId == callId).Result?.ToString() ?? "";
                NativeToolResults.Add(result);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "assistant received " + result);
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NativeExtensionChatClient : IChatClient
    {
        public List<string> Instructions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Instructions.Add(options?.Instructions ?? "");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "conversation completed");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
