using PiSharp.Cli;
using PiSharp.Cli.Composition;
using PiSharp.Cli.Sessions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ConversationCompositionTests
{
    [Theory]
    [InlineData("default", "grep,inactive_tool,read")]
    [InlineData("settings", "grep,inactive_tool,read")]
    [InlineData("explicit", "read,write")]
    [InlineData("modifiers", "inactive_tool,read,write")]
    [InlineData("excluded", "grep,read")]
    [InlineData("disabled", "")]
    [InlineData("imported", "grep,inactive_tool,read")]
    [InlineData("imported-empty", "")]
    [InlineData("imported-update", "inactive_tool,read,write")]
    [InlineData("imported-after-custom", "grep,inactive_tool,read")]
    [InlineData("fresh", "active_tool,bash,edit,read,write")]
    [InlineData("fresh-modifiers", "active_tool,bash,edit,read")]
    public async Task ResumeHonorsToolSelectionAndTheRecordedLoadout(string mode, string expected)
    {
        string[] flags = mode switch
        {
            "explicit" => ["--tools", "read,write"],
            "modifiers" => ["--tools", "-grep,+write"],
            "fresh-modifiers" => ["--tools", "-write,-active_tool"],
            "excluded" => ["--exclude-tools", "inactive_tool"],
            "disabled" => ["--no-tools"],
            _ => []
        };
        await WithRuntimeAsync(async (root, project, model, conversation, runs, transitions) =>
        {
            var session = conversation.Conversation;
            if (mode.StartsWith("imported", StringComparison.Ordinal))
            {
                if (mode == "imported-after-custom") session.AppendToolLoadout(["ls"]);
                var message = new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, "instructions");
                SystemMessageTranscript.ImportMetadata(message, System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    toolsAdded = (mode == "imported-empty" ? [] : new[] { "read", "grep", "inactive_tool" }).Select(name => new { name })
                }));
                session.Append(message);
                if (mode == "imported-update")
                {
                    var update = new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, "updated");
                    SystemMessageTranscript.ImportMetadata(update, System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        toolsAdded = new[] { new { name = "write" } },
                        toolsRemoved = new[] { new { name = "grep" } }
                    }));
                    session.Append(update);
                }
                session = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(session));
            }
            else if (!mode.StartsWith("fresh", StringComparison.Ordinal)) session.AppendToolLoadout(["read", "grep", "inactive_tool"]);
            var resumed = await runs.OpenAsync(model, model.Agent, session, null);
            Assert.Equal(expected.Length == 0 ? [] : expected.Split(','), resumed.ActiveToolNames.Order(StringComparer.Ordinal));
        }, flags, mode == "settings" ? new UserSettings(DefaultTools: ["read"]) : new UserSettings());
    }

    [Fact]
    public async Task OpenRunReadsCurrentSettingsAndKeepsItsOriginalPersistenceDestination()
    {
        await WithRuntimeAsync(async (root, project, model, conversation, runs, transitions) =>
        {
            project.SetSettings(new(), null, new UserSettings
            {
                SteeringMode = PromptDeliveryMode.All,
                FollowUpMode = PromptDeliveryMode.All
            });
            var path = Path.Combine(root, "original.session.json");
            var run = await runs.OpenAsync(model, model.Agent, conversation.Conversation, path,
                activeToolNamesOverride: ["read"]);
            Assert.Equal(PromptDeliveryMode.All, run.SteeringMode);
            Assert.Equal(PromptDeliveryMode.All, run.FollowUpMode);
            Assert.Equal(new[] { "read" }, run.ActiveToolNames);
            var disk = await project.Store.LoadAsync(path);
            Assert.Equal(new[] { "read" }, disk.ActiveToolLoadout());

            // Changing the active conversation/path does not redirect an already opened run's save.
            conversation.Adopt(new ConversationSession(root, model.Connection.Model, null), run, Path.Combine(root, "later.session.json"));
            run.Conversation.Rename("original run");
            await run.PersistAsync();
            Assert.Equal("original run", (await project.Store.LoadAsync(path)).Name);
            Assert.False(File.Exists(conversation.Path));
        });
    }

    [Fact]
    public async Task ReloadPublishesProjectBeforeUiRefreshAndExecutionBeforeHistoryReload()
    {
        await WithRuntimeAsync(async (root, project, model, conversation, runs, transitions) =>
        {
            var oldAgent = model.Agent;
            var oldRun = conversation.Run;
            var oldProject = project.Runtime;
            Directory.CreateDirectory(Path.Combine(root, ".pi"));
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"),
                "{\"steeringMode\":\"all\",\"followUpMode\":\"all\"}");
            var events = new List<string>();
            await transitions.ReloadAsync(false, replacement =>
            {
                events.Add("prepare");
                Assert.Same(oldProject, project.Runtime);
            }, () =>
            {
                events.Add("refresh");
                Assert.NotSame(oldProject, project.Runtime);
                Assert.Same(oldAgent, model.Agent);
                Assert.Same(oldRun, conversation.Run);
            }, () =>
            {
                events.Add("history");
                Assert.NotSame(oldAgent, model.Agent);
                Assert.NotSame(oldRun, conversation.Run);
                Assert.Equal(PromptDeliveryMode.All, conversation.Run.SteeringMode);
                Assert.Equal(PromptDeliveryMode.All, conversation.Run.FollowUpMode);
            });
            Assert.Equal(new[] { "prepare", "refresh", "history" }, events);
        });
    }

    [Fact]
    public async Task FailedReloadPreparationLeavesTheActiveExecutionAndProjectUsable()
    {
        await WithRuntimeAsync(async (root, project, model, conversation, runs, transitions) =>
        {
            var oldProject = project.Runtime;
            var oldAgent = model.Agent;
            var oldRun = conversation.Run;
            var oldPolicy = model.ContextPolicy;
            await Assert.ThrowsAsync<InvalidOperationException>(() => transitions.ReloadAsync(false,
                _ => throw new InvalidOperationException("presentation failure"),
                () => Assert.Fail("UI must not adopt a failed preparation"),
                () => Assert.Fail("History must not adopt a failed preparation")));
            Assert.Same(oldProject, project.Runtime);
            Assert.Same(oldAgent, model.Agent);
            Assert.Same(oldRun, conversation.Run);
            Assert.Same(oldPolicy, model.ContextPolicy);
            var retry = await runs.OpenAsync(model, model.Agent, conversation.Conversation, null);
            Assert.Contains("read", retry.ActiveToolNames);
        });
    }

    private static async Task WithRuntimeAsync(Func<string, ActiveProjectRuntime, ActiveModelRuntime,
        ActiveConversationRuntime, ConversationRunComposition, ProjectConversationTransitions, Task> body,
        string[]? flags = null, UserSettings? settings = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-conversation-composition-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        try
        {
            var arguments = DefaultToolReloadPolicy.ApplyStartupDefaults(
                CliArguments.Parse(["--local", "--no-extensions", "--no-session", .. flags ?? []]),
                settings ?? new UserSettings(), _ => null, preserveSessionModel: false);
            var trust = new ProjectTrust(agentDirectory);
            using var configuration = await ProjectRuntimeConfiguration.LoadAsync(root, agentDirectory, arguments,
                trust, false, TextReader.Null, TextWriter.Null, trustedOverride: true);
            using var project = await ProjectRuntimeContext.LoadAsync(configuration, agentDirectory, arguments, null);
            project.Extensions.Registration.AddTool(new PiSharpToolRegistration(
                Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "inactive", name: "inactive_tool"), DefaultActive: false));
            project.Extensions.Registration.AddTool(new PiSharpToolRegistration(
                Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "active", name: "active_tool")));
            using var http = new HttpClient();
            var providers = await ProviderModelRuntime.CreateAsync(agentDirectory, true, _ => null, http, offline: true);
            var selection = await providers.ResolveAsync("local", null);
            var activeProject = new ActiveProjectRuntime(configuration, project, arguments);
            var controller = new ModelRuntimeController(providers, () => activeProject.Settings, _ => null);
            var codemode = new ProviderCodemodeModels(providers, http);
            var chat = ProviderChatClientFactory.Create(selection);
            var agent = project.CreateAgent(chat, selection, "off", arguments);
            var model = new ActiveModelRuntime(selection, chat, agent, "off", null, null);
            var runs = new ConversationRunComposition(activeProject, controller);
            var session = new ConversationSession(root, selection.Connection.Model,
                selection.Connection.Endpoint?.ToString(), selection.Provider.Id);
            var conversation = new ActiveConversationRuntime(session, await runs.OpenAsync(model, agent, session, null), null);
            using var extensions = new ExtensionLease(project.TransferExtensions());
            var transitions = new ProjectConversationTransitions(agentDirectory, null, trust, activeProject, model,
                conversation, providers, codemode, runs, extensions);
            await body(root, activeProject, model, conversation, runs, transitions);
        }
        finally { Directory.Delete(root, true); }
    }
}
