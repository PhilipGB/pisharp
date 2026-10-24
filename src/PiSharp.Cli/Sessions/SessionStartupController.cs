using PiSharp.Runtime.Sessions;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Sessions;

internal sealed record StartupConversation(ConversationSession Conversation, string? Path,
    PiSharpExtensionSessionStartReason StartReason);
internal sealed record StartupModelSelection(ModelSelection Selection, string Thinking, bool RebuildRuntime);

internal static class SessionStartupController
{
    public static string? ResolvePath(CliArguments cli, ConversationStore store) =>
        cli.NoSession || cli.ForkSource is not null ? null : cli.SessionPath is not null &&
            (cli.SessionPath.Contains(Path.DirectorySeparatorChar) || cli.SessionPath.EndsWith(".session.json", StringComparison.Ordinal) ||
                cli.SessionPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            ? Path.GetFullPath(cli.SessionPath) : cli.Continue ? store.MostRecentPath() : null;

    public static async Task<StartupConversation> LoadAsync(CliArguments cli, ProjectRuntimeContext projectRuntime,
        ModelSelection selection, string currentDirectory, string? sessionPath)
    {
        var store = projectRuntime.Store;
        var connection = selection.Connection;
        ConversationSession conversation;
        SessionListing? catalogListing = null;
        if (cli.SessionPath is not null && sessionPath is null)
        {
            catalogListing = SessionCatalog.Resolve(await SessionCatalog.ListAsync(store), cli.SessionPath);
            await store.ValidateListingUnchangedAsync(catalogListing);
            sessionPath = catalogListing.Path;
        }
        var resumesExistingSession = sessionPath is not null && File.Exists(sessionPath);
        if (cli.ForkSource is not null)
        {
            var hasExplicitPath = cli.ForkSource.Contains(Path.DirectorySeparatorChar) ||
                cli.ForkSource.EndsWith(".session.json", StringComparison.Ordinal) ||
                cli.ForkSource.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
            var forkListing = hasExplicitPath ? null : await ResolveCatalogSessionAsync(store, cli.ForkSource);
            var sourcePath = hasExplicitPath ? Path.GetFullPath(cli.ForkSource) : forkListing!.Path;
            conversation = await SessionForkFactory.CreateAsync(sourcePath, currentDirectory,
                expectedFingerprint: forkListing?.Fingerprint);
        }
        else if (sessionPath is not null && sessionPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            conversation = projectRuntime.SessionImport.ImportFile(sessionPath, catalogListing?.Fingerprint);
            sessionPath = store.NewPath(conversation);
            await store.SaveAsync(conversation, sessionPath);
        }
        else
            conversation = sessionPath is not null && File.Exists(sessionPath)
                ? catalogListing is not null
                    ? await store.LoadAsync(catalogListing)
                    : await store.LoadAsync(sessionPath)
                : new ConversationSession(currentDirectory, connection.Model, connection.Endpoint?.ToString(), selection.Provider.Id);
        PiJsonlSessionInterchange.RestoreRegisteredVirtualModelSelection(conversation,
            projectRuntime.Extensions.Registration.VirtualModels);
        if (conversation.Model == "unknown")
        {
            conversation.SelectModel(connection.Model, connection.Endpoint?.ToString(), selection.Provider.Id);
            if (sessionPath is not null) await store.SaveAsync(conversation, sessionPath);
        }
        var reason = cli.ForkSource is not null
            ? PiSharpExtensionSessionStartReason.Fork
            : resumesExistingSession ? PiSharpExtensionSessionStartReason.Resume : PiSharpExtensionSessionStartReason.Startup;
        return new(conversation, sessionPath, reason);
    }

    private static async Task<SessionListing> ResolveCatalogSessionAsync(ConversationStore store, string reference)
    {
        var listing = SessionCatalog.Resolve(await SessionCatalog.ListAsync(store), reference);
        await store.ValidateListingUnchangedAsync(listing);
        return listing;
    }

    public static async Task<StartupModelSelection> ResolveModelAsync(ConversationSession conversation,
        ModelSelection selection, string thinking, CliArguments cli, string? explicitThinking, UserSettings userSettings,
        ProviderModelRuntime modelRuntime)
    {
        var connection = selection.Connection;
        var explicitConnection = cli.Local || cli.Provider is not null || cli.ModelOverride is not null ||
            Environment.GetEnvironmentVariable("PISHARP_MODEL") is not null || Environment.GetEnvironmentVariable("PISHARP_BASE_URL") is not null;
        var savedThinking = !explicitConnection && cli.Thinking is null
            ? PiJsonlSessionInterchange.GetThinkingLevel(conversation) : null;
        if (savedThinking is not null) thinking = savedThinking;
        var savedProvider = conversation.Provider ?? modelRuntime.Providers.FirstOrDefault(item =>
            string.Equals(item.Endpoint.ToString().TrimEnd('/'), conversation.Endpoint?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))?.Id ??
            (conversation.Endpoint is null ? "openai" : null);
        if (savedProvider is null)
            throw new InvalidDataException("Session provider cannot be resolved from models.json or the saved endpoint.");
        if (explicitConnection && (conversation.Model != connection.Model || conversation.Endpoint != connection.Endpoint?.ToString() ||
            conversation.Provider is not null && !conversation.Provider.Equals(selection.Provider.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Requested provider/model conflicts with the saved session model or endpoint. Open it without explicit model flags and use /model after opening.");
        if (!explicitConnection && (conversation.Model != connection.Model || conversation.Endpoint != connection.Endpoint?.ToString() ||
            !savedProvider.Equals(selection.Provider.Id, StringComparison.OrdinalIgnoreCase) || savedThinking is not null))
        {
            selection = await modelRuntime.ResolveAsync(savedProvider, conversation.Model);
            if (savedThinking is null && explicitThinking is null)
                thinking = userSettings.GetModelThinkingLevel(selection) ?? cli.Thinking ?? thinking;
            return new(selection, thinking, true);
        }
        return new(selection, thinking, false);
    }
}
