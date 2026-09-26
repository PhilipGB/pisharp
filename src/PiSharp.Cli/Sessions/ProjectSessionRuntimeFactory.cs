using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal sealed class ProjectSessionRuntimeFactory(
    string agentDirectory,
    CliArguments arguments,
    string? configuredSessionDirectory,
    ProjectTrust trustStore,
    ProviderModelRuntime modelRuntime)
{
    public async Task<ProjectSessionRuntime> OpenAsync(string sessionPath, string invocationDirectory,
        ModelSelection currentSelection, string currentThinking, bool keepSourceSessionDirectory = false,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(sessionPath, invocationDirectory);
        if (!File.Exists(path)) throw new FileNotFoundException("Session file was not found.", path);
        var workingDirectory = PiSessionStartupTarget.ReadWorkingDirectory(path, invocationDirectory);
        var configuration = await ProjectRuntimeConfiguration.LoadAsync(workingDirectory, agentDirectory, arguments,
            trustStore, interactiveTrust: false, TextReader.Null, TextWriter.Null, cancellationToken: cancellationToken);
        var sourceSessionDirectory = keepSourceSessionDirectory ? Path.GetDirectoryName(path) : null;
        var project = await ProjectRuntimeContext.LoadAsync(configuration, agentDirectory, arguments,
            configuredSessionDirectory, sourceSessionDirectory, cancellationToken);
        var createdImportedSession = false;
        string? destinationPath = null;
        try
        {
            var isPiJsonl = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
            var conversation = isPiJsonl
                ? project.SessionImport.ImportFile(path)
                : await project.Store.LoadAsync(path, cancellationToken);
            destinationPath = arguments.NoSession ? null : isPiJsonl
                ? project.SessionImport.CreateDestinationPath(conversation)
                : path;

            var selection = conversation.Model == "unknown"
                ? currentSelection
                : await modelRuntime.ResolveAsync(ResolveProvider(conversation), conversation.Model,
                    cancellationToken, includeOutOfScope: true);
            if (conversation.Model == "unknown")
                conversation.SelectModel(selection.Connection.Model, selection.Connection.Endpoint?.ToString(), selection.Provider.Id);

            var thinking = ThinkingLevels.ValidateForModel(
                PiJsonlSessionInterchange.GetThinkingLevel(conversation) ?? currentThinking,
                selection.Model.Reasoning);
            var chat = ProviderChatClientFactory.Create(selection);
            var agent = project.CreateAgent(chat, selection, thinking, arguments);
            var compaction = project.Settings.ResolveCompaction(selection.Model.ContextLength,
                Environment.GetEnvironmentVariable, $"{selection.Provider.Id}/{selection.Model.Id}");
            var pricing = ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? selection.Model.Pricing;

            if (destinationPath is not null && (isPiJsonl || conversation.Model == "unknown"))
            {
                await project.Store.SaveAsync(conversation, destinationPath, cancellationToken);
                createdImportedSession = isPiJsonl;
            }

            var run = await ConversationRun.OpenAsync(agent, conversation, cancellationToken,
                save: destinationPath is null ? null : token => project.Store.SaveAsync(conversation, destinationPath, token),
                autoCompaction: compaction, pricing: pricing, sessionFile: destinationPath,
                provider: selection.Provider.Id, reasoningLevel: thinking,
                retryPolicy: project.Settings.Retry?.ResolvePolicy() ?? AgentRunRetryPolicy.Default,
                steeringMode: project.Settings.SteeringMode ?? PromptDeliveryMode.OneAtATime,
                followUpMode: project.Settings.FollowUpMode ?? PromptDeliveryMode.OneAtATime);
            return new ProjectSessionRuntime(project, conversation, run, destinationPath, selection, chat, agent,
                thinking, compaction, pricing);
        }
        catch
        {
            if (createdImportedSession && destinationPath is not null)
            {
                try { File.Delete(destinationPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                try { File.Delete(destinationPath + ".lock"); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            project.Dispose();
            throw;
        }
    }

    private string ResolveProvider(ConversationSession conversation) =>
        conversation.Provider ?? modelRuntime.Providers.FirstOrDefault(item =>
            string.Equals(item.Endpoint.ToString().TrimEnd('/'), conversation.Endpoint?.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))?.Id ??
        (conversation.Endpoint is null ? "openai" :
            throw new InvalidDataException("Session provider cannot be resolved from models.json or the saved endpoint."));
}

internal sealed class ProjectSessionRuntime(ProjectRuntimeContext project, ConversationSession conversation,
    ConversationRun run, string? path, ModelSelection selection, IChatClient chat, PiAgent agent, string thinking,
    AutoCompactionPolicy? contextPolicy, ModelPricing? pricing) : IDisposable
{
    public ProjectRuntimeContext Project { get; } = project;
    public ConversationSession Conversation { get; } = conversation;
    public ConversationRun Run { get; } = run;
    public string? Path { get; } = path;
    public ModelSelection Selection { get; } = selection;
    public IChatClient Chat { get; } = chat;
    public PiAgent Agent { get; } = agent;
    public string Thinking { get; } = thinking;
    public AutoCompactionPolicy? ContextPolicy { get; } = contextPolicy;
    public ModelPricing? Pricing { get; } = pricing;

    public void Dispose() => Project.Dispose();
}
