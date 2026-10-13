using PiSharp.Cli;
using PiSharp.Cli.Composition;
using PiSharp.Cli.Sessions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ConversationCompositionTests
{
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
        ActiveConversationRuntime, ConversationRunComposition, ProjectConversationTransitions, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-conversation-composition-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        try
        {
            var arguments = CliArguments.Parse(["--local", "--no-extensions", "--no-session"]);
            var trust = new ProjectTrust(agentDirectory);
            using var configuration = await ProjectRuntimeConfiguration.LoadAsync(root, agentDirectory, arguments,
                trust, false, TextReader.Null, TextWriter.Null, trustedOverride: true);
            using var project = await ProjectRuntimeContext.LoadAsync(configuration, agentDirectory, arguments, null);
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
