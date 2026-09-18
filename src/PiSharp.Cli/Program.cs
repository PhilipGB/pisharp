using System.Text.Json;
using PiSharp.Core;
using PiSharp.Core.Models;
using PiSharp.Core.Models.Providers;
using PiSharp.Core.Settings;
using PiSharp.Cli;

try
{
    var options = CliOptions.Parse(args);
    if (options.ShowHelp)
    {
        PrintHelp();
        return 0;
    }

    if (!Directory.Exists(options.WorkingDirectory))
    {
        Console.Error.WriteLine($"Workspace does not exist: {options.WorkingDirectory}");
        return 2;
    }

    using var shutdown = new CancellationTokenSource();
    LiveTurnCoordinator? liveTurns = null;
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        if (liveTurns?.IsRunning == true)
        {
            liveTurns.Abort();
            return;
        }

        shutdown.Cancel();
    };

    var trustStore = new ProjectTrustStore(ProjectTrustPath.GetDefaultTrustDirectory());
    // Global settings load before trust resolution: defaultProjectTrust is a global-scope
    // value. Project settings are picked up once the trust decision is known.
    var settings = await SettingsManager.CreateAsync(options.WorkingDirectory, projectTrusted: false);
    foreach (var diagnostic in settings.DrainDiagnostics())
    {
        Console.Error.WriteLine($"Warning: {diagnostic.RenderMessage()}");
    }

    var trustResolution = new ProjectTrustResolver().Resolve(
        options.WorkingDirectory,
        trustStore,
        options.ProjectTrustOverride,
        settings.GetDefaultProjectTrust(),
        IsInteractiveStartup(options) ? ProjectTrustMode.Interactive : ProjectTrustMode.NonInteractive,
        IsInteractiveStartup(options) ? SelectProjectTrustOption : null);
    await settings.SetProjectTrustedAsync(trustResolution.Trusted);
    var keybindings = await KeybindingsManager.CreateAsync();

    // Model runtime + startup resolution (pinned main.ts): built-in providers, models.json,
    // auth.json, the local endpoint provider, scoped models, and the CLI model/thinking.
    var startup = await ModelStartup.CreateAsync(options, settings, shutdown.Token);
    foreach (var warning in startup.Warnings)
    {
        Console.Error.WriteLine($"Warning: {warning}");
    }

    if (options.ListModels)
    {
        ModelCatalogPrinter.Print(startup.Runtime);
        return 0;
    }

    if (startup.Error is not null)
    {
        Console.Error.WriteLine($"Error: {startup.Error}");
        return 1;
    }

    var bootstrap = await AgentFactory.CreateAsync(
        options, trustResolution.Trusted, shutdown.Token, startup.Runtime, startup.ModelState, settings: settings);
    liveTurns = new LiveTurnCoordinator(bootstrap.TurnQueue);
    var sessions = await SessionController.CreateAsync(bootstrap, options, shutdown.Token, settings);
    // Manual compaction aborts the active turn before compacting (Pi semantics).
    sessions.AbortActiveTurn = liveTurns.Abort;

    // Pinned main.ts: non-interactive modes require a resolvable model at startup; interactive
    // mode continues and reports "No model selected" at the prompt. The no-models message
    // itself was already reported by the startup resolution above.
    if (bootstrap.ModelState.Model is null &&
        (options.OutputMode != OutputMode.Text || options.PrintMode || options.Prompt is not null))
    {
        return 1;
    }

    if (options.OutputMode == OutputMode.Rpc)
    {
        return await HeadlessModes.RunRpcModeAsync(bootstrap, sessions, liveTurns, options, shutdown.Token);
    }

    if (options.OutputMode == OutputMode.Json || options.PrintMode ||
        (options.Prompt is null && Console.IsInputRedirected))
    {
        return await HeadlessModes.RunPrintModeAsync(bootstrap, sessions, liveTurns, options, shutdown.Token);
    }

    if (options.Prompt is not null || options.FilePaths.Count > 0)
    {
        var promptInput = await FileArgumentLoader.LoadAsync(
            options.Prompt,
            options.FilePaths,
            options.WorkingDirectory,
            shutdown.Token);
        var singlePromptOutput = new TerminalChatOutput();
        sessions.EventOutput = singlePromptOutput;
        var result = await AgentTurnRunner.RunAsync(
            bootstrap,
            sessions,
            liveTurns,
            promptInput.Text,
            options.WorkingDirectory,
            singlePromptOutput,
            shutdown.Token,
            promptInput.Images);
        await sessions.PersistTurnAsync(
            promptInput.Text,
            result.AssistantText,
            shutdown.Token,
            result.ToolRecords);
        await PublishShutdownAsync(bootstrap.ExtensionHost, options.WorkingDirectory, bootstrap.TurnQueue);
        return result.Cancelled ? 130 : 0;
    }

    Console.WriteLine($"PiSharp  |  {sessions.ModelState.Model?.Reference ?? "no model"}  |  {options.WorkingDirectory}");
    if (sessions.ModelState.Model is null)
    {
        Console.WriteLine("No model selected. Set an API key, use /login, or select a model with /model.");
    }
    if (trustResolution.TrustRequired && !trustResolution.Trusted)
    {
        Console.WriteLine("This project is not trusted. Project resources are ignored; use /trust and restart PiSharp.");
    }
    Console.WriteLine(sessions.FormatSessionInfo());
    if (bootstrap.ContextFiles.Count > 0)
    {
        Console.WriteLine($"Context files: {bootstrap.ContextFiles.Count} (use /context to list)");
    }
    Console.WriteLine("Type /help for commands. Ctrl+C aborts the active turn; press it again to exit.");

    using var promptReader = new TerminalPromptReader(
        Console.In,
        Console.Out,
        enableBracketedPaste: !Console.IsInputRedirected && !Console.IsOutputRedirected);
    var interactiveOutput = new TerminalChatOutput();
    sessions.EventOutput = interactiveOutput;
    await RunInteractiveAsync(
        bootstrap,
        sessions,
        options.WorkingDirectory,
        liveTurns,
        interactiveOutput,
        promptReader,
        trustStore,
        settings,
        keybindings,
        shutdown.Token);
    await PublishShutdownAsync(bootstrap.ExtensionHost, options.WorkingDirectory, bootstrap.TurnQueue);

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static Task PublishShutdownAsync(
    PiSharpExtensionHost extensionHost,
    string workspaceRoot,
    TurnMessageQueue turnQueue) =>
    extensionHost.PublishAsync(
        PiSharpExtensionEvent.Shutdown,
        new PiSharpExtensionContext(workspaceRoot, turnQueue, CancellationToken.None, Console.WriteLine));

static async Task RunInteractiveAsync(
    AgentBootstrap bootstrap,
    SessionController sessions,
    string workspaceRoot,
    LiveTurnCoordinator liveTurns,
    IChatOutput output,
    TerminalPromptReader promptReader,
    ProjectTrustStore trustStore,
    SettingsManager settings,
    KeybindingsManager keybindings,
    CancellationToken cancellationToken)
{
    Task<string?>? pendingInput = null;
    while (!cancellationToken.IsCancellationRequested)
    {
        pendingInput ??= ReadInputAsync(promptReader, cancellationToken);
        var input = await pendingInput;
        pendingInput = null;
        if (input is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!input.Contains('\n') && input.StartsWith("/", StringComparison.Ordinal) &&
            await HandleCommandAsync(
                input,
                sessions,
                bootstrap,
                output,
                bootstrap.ContextFiles,
                bootstrap.ExtensionHost,
                workspaceRoot,
                liveTurns.Queue,
                trustStore,
                settings,
                keybindings,
                cancellationToken))
        {
            continue;
        }

        var activeTurn = AgentTurnRunner.RunAsync(
            bootstrap,
            sessions,
            liveTurns,
            input,
            workspaceRoot,
            output,
            cancellationToken);
        var expandInput = AgentTurnRunner.CreateInputExpander(bootstrap, bootstrap.ExtensionHost);
        var activeInput = await DrainActiveInputAsync(
            activeTurn,
            liveTurns,
            pendingInput,
            promptReader,
            expandInput,
            cancellationToken);
        pendingInput = activeInput.PendingInput;
        LiveTurnResult result;
        try
        {
            result = await activeTurn;
        }
        catch (SessionOperationConflictException exception)
        {
            Console.Error.WriteLine(exception.Message);
            continue;
        }
        await sessions.PersistTurnAsync(input, result.AssistantText, cancellationToken, result.ToolRecords);
        if (activeInput.ShouldExit)
        {
            return;
        }
    }
}

static async Task<(bool ShouldExit, Task<string?>? PendingInput)> DrainActiveInputAsync(
    Task<LiveTurnResult> activeTurn,
    LiveTurnCoordinator liveTurns,
    Task<string?>? pendingInput,
    TerminalPromptReader promptReader,
    Func<string, string> expandInput,
    CancellationToken cancellationToken)
{
    while (!activeTurn.IsCompleted && !cancellationToken.IsCancellationRequested)
    {
        pendingInput ??= ReadInputAsync(promptReader, cancellationToken);
        var completed = await Task.WhenAny(activeTurn, pendingInput);
        if (completed == activeTurn)
        {
            return (false, pendingInput);
        }

        var input = await pendingInput;
        pendingInput = null;
        if (input is null)
        {
            liveTurns.Abort();
            return (true, pendingInput);
        }

        if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            liveTurns.Abort();
            return (true, pendingInput);
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        QueueActiveInput(input, liveTurns, expandInput);
    }

    return (false, pendingInput);
}

static void QueueActiveInput(string input, LiveTurnCoordinator liveTurns, Func<string, string> expandInput)
{
    const string SteeringPrefix = "/steer ";
    const string FollowUpPrefix = "/follow-up ";
    const string FollowUpAlias = "/followup ";

    if (input.StartsWith(FollowUpPrefix, StringComparison.OrdinalIgnoreCase))
    {
        liveTurns.Queue.EnqueueFollowUp(expandInput(input[FollowUpPrefix.Length..]));
        Console.WriteLine("[queued follow-up]");
        return;
    }

    if (input.StartsWith(FollowUpAlias, StringComparison.OrdinalIgnoreCase))
    {
        liveTurns.Queue.EnqueueFollowUp(expandInput(input[FollowUpAlias.Length..]));
        Console.WriteLine("[queued follow-up]");
        return;
    }

    var steering = input.StartsWith(SteeringPrefix, StringComparison.OrdinalIgnoreCase)
        ? input[SteeringPrefix.Length..]
        : input;
    liveTurns.Queue.EnqueueSteering(expandInput(steering));
    Console.WriteLine("[queued steering message]");
}

static Task<string?> ReadInputAsync(TerminalPromptReader promptReader, CancellationToken cancellationToken) =>
    Task.Run(() => promptReader.ReadPrompt(), cancellationToken);

static async Task<bool> HandleCommandAsync(
    string input,
    SessionController sessions,
    AgentBootstrap bootstrap,
    IChatOutput output,
    IReadOnlyList<string> contextFiles,
    PiSharpExtensionHost extensionHost,
    string workspaceRoot,
    TurnMessageQueue turnQueue,
    ProjectTrustStore trustStore,
    SettingsManager settings,
    KeybindingsManager keybindings,
    CancellationToken cancellationToken)
{
    var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var command = parts[0].ToLowerInvariant();
    var argument = parts.Length > 1 ? parts[1] : null;

    switch (command)
    {
        case "/help":
            PrintInteractiveHelp();
            return true;
        case "/settings":
            if (argument is null)
            {
                SettingsCommands.PrintStatus(settings);
                return true;
            }
            try
            {
                Console.WriteLine(await SettingsCommands.ApplyArgumentAsync(settings, argument));
            }
            catch (FormatException exception)
            {
                Console.Error.WriteLine(exception.Message);
            }
            return true;
        case "/reload":
            await settings.ReloadAsync(cancellationToken);
            await keybindings.ReloadAsync(cancellationToken);
            foreach (var diagnostic in settings.DrainDiagnostics())
            {
                Console.Error.WriteLine($"Warning: {diagnostic.RenderMessage()}");
            }
            Console.WriteLine("Reloaded settings and keybindings.");
            return true;
        case "/session":
            Console.WriteLine(sessions.FormatSessionInfo());
            return true;
        case "/name":
            if (string.IsNullOrWhiteSpace(argument))
            {
                var currentName = sessions.Document?.Name;
                Console.WriteLine(string.IsNullOrWhiteSpace(currentName)
                    ? "Usage: /name <session-name>"
                    : $"Session name: {currentName}");
                return true;
            }
            await sessions.SetNameAsync(argument, cancellationToken);
            Console.WriteLine(sessions.FormatSessionInfo());
            return true;
        case "/label":
            var labelTarget = argument?.Trim();
            if (string.IsNullOrWhiteSpace(labelTarget))
            {
                Console.WriteLine("Usage: /label <entry-id> [text]   (omit text to clear the label)");
                return true;
            }
            var labelSeparator = labelTarget.IndexOf(' ');
            var labelSelector = labelSeparator < 0 ? labelTarget : labelTarget[..labelSeparator].Trim();
            var labelText = labelSeparator < 0 ? null : labelTarget[(labelSeparator + 1)..].Trim();
            var labelValue = await sessions.SetLabelAsync(labelSelector, labelText, cancellationToken);
            Console.WriteLine(labelValue is null
                ? $"Cleared label on {labelSelector}."
                : $"Labeled {labelSelector}: {labelValue}");
            return true;
        case "/model":
            await HandleModelCommandAsync(argument, sessions, bootstrap, cancellationToken);
            return true;
        case "/thinking":
            await HandleThinkingCommandAsync(argument, sessions, cancellationToken);
            return true;
        case "/stats":
            Console.WriteLine(JsonSerializer.Serialize(sessions.GetStatistics()));
            return true;
        case "/tree":
            Console.WriteLine(sessions.FormatTree());
            return true;
        case "/goto":
            if (string.IsNullOrWhiteSpace(argument))
            {
                Console.WriteLine("Usage: /goto <entry-id|root> [--summarize]");
                return true;
            }
            var navigationParts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var summarizeBranch = navigationParts.Any(part => part.Equals("--summarize", StringComparison.OrdinalIgnoreCase));
            var targetSelector = navigationParts.FirstOrDefault(part => !part.StartsWith("--", StringComparison.Ordinal)) ?? "root";
            await sessions.NavigateAsync(targetSelector, summarizeBranch, null, output, cancellationToken);
            Console.WriteLine($"Checked out {targetSelector}. The next prompt will branch from this point.");
            return true;
        case "/compact":
            await sessions.CompactAsync(argument, CompactionReason.Manual, output, cancellationToken);
            Console.WriteLine("Compaction complete.");
            return true;
        case "/fork":
            await sessions.ForkAsync(argument, cancellationToken);
            Console.WriteLine("Forked active path into a new session.");
            Console.WriteLine(sessions.FormatSessionInfo());
            return true;
        case "/clone":
            await sessions.ForkAsync(null, cancellationToken);
            Console.WriteLine("Cloned the current active branch into a new session.");
            Console.WriteLine(sessions.FormatSessionInfo());
            return true;
        case "/new":
            await sessions.NewAsync(cancellationToken);
            Console.WriteLine("Started a new session.");
            Console.WriteLine(sessions.FormatSessionInfo());
            return true;
        case "/resume":
            if (await sessions.ResumeInteractiveAsync(cancellationToken))
            {
                Console.WriteLine("Resumed session.");
                Console.WriteLine(sessions.FormatSessionInfo());
            }
            return true;
        case "/trust":
            await SaveInteractiveTrustDecisionAsync(workspaceRoot, trustStore);
            return true;
        case "/context":
            if (contextFiles.Count == 0)
            {
                Console.WriteLine("No context files loaded.");
            }
            else
            {
                Console.WriteLine("Loaded context files:");
                foreach (var file in contextFiles)
                {
                    Console.WriteLine($"  {file}");
                }
            }
            return true;
        default:
            var extensionResult = await extensionHost.ExecuteCommandAsync(
                input,
                new PiSharpExtensionContext(workspaceRoot, turnQueue, cancellationToken, Console.WriteLine));
            if (extensionResult.Message is not null)
            {
                Console.WriteLine(extensionResult.Message);
            }
            return extensionResult.Handled;
    }
}

/// <summary>
/// /model (pinned handleModelCommand): no argument lists the selectable models; an exact
/// reference selects it (refreshing remote catalogs first when the cache misses, pinned
/// findExactModelMatch); next/prev cycle (scoped models first, pinned cycleModel). The
/// trailing --persist flag mirrors the selector's save-as-default action.
/// </summary>
static async Task HandleModelCommandAsync(
    string? argument,
    SessionController sessions,
    AgentBootstrap bootstrap,
    CancellationToken cancellationToken)
{
    var modelState = bootstrap.ModelState;
    if (argument is null)
    {
        PrintModelList(modelState, bootstrap.ModelRuntime);
        return;
    }

    var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var persist = parts.Any(part => part.Equals("--persist", StringComparison.OrdinalIgnoreCase));
    var term = parts.FirstOrDefault(part => !part.Equals("--persist", StringComparison.OrdinalIgnoreCase));
    if (term is null)
    {
        PrintModelList(modelState, bootstrap.ModelRuntime);
        return;
    }

    if (term.Equals("next", StringComparison.OrdinalIgnoreCase) ||
        term.Equals("forward", StringComparison.OrdinalIgnoreCase) ||
        term.Equals("prev", StringComparison.OrdinalIgnoreCase) ||
        term.Equals("previous", StringComparison.OrdinalIgnoreCase) ||
        term.Equals("back", StringComparison.OrdinalIgnoreCase) ||
        term.Equals("backward", StringComparison.OrdinalIgnoreCase))
    {
        var direction = term.Equals("next", StringComparison.OrdinalIgnoreCase) ||
            term.Equals("forward", StringComparison.OrdinalIgnoreCase) ? "forward" : "backward";
        var cycled = await sessions.CycleModelAsync(direction, new ModelMutationOptions(persist), cancellationToken);
        Console.WriteLine(cycled is null
            ? "Only one model is available to cycle."
            : $"Model: {cycled.Model.Id}");
        return;
    }

    var model = await FindModelForCommandAsync(term, modelState, bootstrap.ModelRuntime, cancellationToken);
    if (model is null)
    {
        Console.Error.WriteLine($"Unknown model \"{term}\".");
        PrintModelList(modelState, bootstrap.ModelRuntime);
        return;
    }

    try
    {
        await sessions.SetModelAsync(model, new ModelMutationOptions(persist), cancellationToken);
        Console.WriteLine(persist ? $"Default model: {model.Reference}" : $"Model: {model.Id}");
    }
    catch (InvalidOperationException exception)
    {
        Console.Error.WriteLine(exception.Message);
    }
}

/// <summary>
/// Resolves a /model argument exactly (pinned findExactModelReferenceMatch) against the
/// scoped models when a scope is active, otherwise the available snapshot; on a cache miss
/// without a scope, refreshes the remote catalogs once with the pinned 15s cap and retries
/// before giving up.
/// </summary>
static async Task<ModelInfo?> FindModelForCommandAsync(
    string term,
    ModelSessionState modelState,
    ModelRuntime runtime,
    CancellationToken cancellationToken)
{
    var candidates = modelState.ScopedModels.Count > 0
        ? modelState.ScopedModels.Select(scoped => scoped.Model).ToList()
        : runtime.GetAvailableSnapshot().ToList();
    var match = ModelResolver.FindExactModelReferenceMatch(term, candidates);
    if (match is not null || modelState.ScopedModels.Count > 0)
    {
        return match;
    }

    Console.WriteLine("Refreshing model catalogs\u2026");
    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var result = await runtime.RefreshAsync(new ModelsRefreshOptions { CancellationToken = cts.Token });
        if (result.Aborted)
        {
            Console.Error.WriteLine("Warning: model refresh timed out; searching cached models.");
        }
        else if (result.Errors.Count > 0)
        {
            Console.Error.WriteLine(
                $"Warning: could not refresh {string.Join(", ", result.Errors.Keys)}; searching cached models.");
        }
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        Console.Error.WriteLine("Warning: model refresh timed out; searching cached models.");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Warning: could not refresh model catalogs: {exception.Message}");
    }

    return ModelResolver.FindExactModelReferenceMatch(term, runtime.GetAvailableSnapshot());
}

/// <summary>
/// Lists the selectable models (scoped when a scope is active, otherwise the authenticated
/// snapshot) with the current model marked.
/// </summary>
static void PrintModelList(ModelSessionState modelState, ModelRuntime runtime)
{
    var candidates = modelState.ScopedModels.Count > 0
        ? modelState.ScopedModels.Select(scoped => scoped.Model).ToList()
        : runtime.GetAvailableSnapshot().ToList();

    var current = modelState.Model;
    var currentLevel = modelState.ThinkingLevel ?? "off";
    Console.WriteLine(current is { } selected
        ? $"Current model: {selected.Reference}  (thinking: {currentLevel})"
        : "No model selected.");
    if (candidates.Count == 0)
    {
        Console.WriteLine("No models available. Set an API key or use /login.");
        return;
    }

    foreach (var provider in candidates.Select(model => model.Provider).Distinct().OrderBy(p => p, StringComparer.Ordinal))
    {
        Console.WriteLine($"  {provider}:");
        foreach (var model in candidates.Where(m => m.Provider == provider).OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var marker = current is not null && string.Equals(model.Reference, current.Reference, StringComparison.Ordinal)
                ? "  [current]"
                : string.Empty;
            var thinking = model.Reasoning ? "  thinking" : string.Empty;
            Console.WriteLine($"    {model.Id}{thinking}{marker}");
        }
    }
}

/// <summary>
/// /thinking (pinned handleThinkingCommand): no argument lists the selectable levels; an
/// exact level name selects it (clamped by setThinkingLevel); next cycles through the
/// current model's levels. The trailing --persist flag mirrors the selector's save-as-default
/// action.
/// </summary>
static async Task HandleThinkingCommandAsync(
    string? argument,
    SessionController sessions,
    CancellationToken cancellationToken)
{
    var modelState = sessions.ModelState;
    if (argument is null)
    {
        PrintThinkingLevels(modelState);
        return;
    }

    var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var persist = parts.Any(part => part.Equals("--persist", StringComparison.OrdinalIgnoreCase));
    var term = parts.FirstOrDefault(part => !part.Equals("--persist", StringComparison.OrdinalIgnoreCase));
    if (term is null)
    {
        PrintThinkingLevels(modelState);
        return;
    }

    if (term.Equals("next", StringComparison.OrdinalIgnoreCase))
    {
        var level = await sessions.CycleThinkingLevelAsync(new ModelMutationOptions(persist), cancellationToken);
        Console.WriteLine(level is null
            ? "Current model does not support thinking."
            : $"Thinking level: {level}");
        return;
    }

    var available = modelState.GetAvailableThinkingLevels();
    var match = available.FirstOrDefault(level => string.Equals(level, term, StringComparison.OrdinalIgnoreCase));
    if (match is null)
    {
        Console.Error.WriteLine(
            $"Unknown thinking level \"{term}\". Available levels: {string.Join(", ", available)}.");
        return;
    }

    var result = await sessions.SetThinkingLevelAsync(match, new ModelMutationOptions(persist), cancellationToken);
    Console.WriteLine(persist ? $"Default thinking level: {match}" : $"Thinking level: {result.Effective}");
}

/// <summary>Shows the current thinking level and the levels selectable for the model.</summary>
static void PrintThinkingLevels(ModelSessionState modelState)
{
    var level = modelState.ThinkingLevel ?? "off";
    Console.WriteLine(modelState.Model is null
        ? "No model selected; thinking levels default to the selectable options."
        : $"Thinking level: {level}");
    Console.WriteLine($"Available levels: {string.Join(", ", modelState.GetAvailableThinkingLevels())}");
}

static bool IsInteractiveStartup(CliOptions options) =>
    options.OutputMode == OutputMode.Text &&
    !options.PrintMode &&
    options.Prompt is null &&
    options.FilePaths.Count == 0 &&
    !Console.IsInputRedirected &&
    !Console.IsOutputRedirected;

static ProjectTrustOption? SelectProjectTrustOption(IReadOnlyList<ProjectTrustOption> options)
{
    Console.WriteLine("Trust project folder?");
    Console.WriteLine("This allows PiSharp to load project settings and resources and execute project extensions.");
    for (var index = 0; index < options.Count; index++)
    {
        Console.WriteLine($"  {index + 1}. {options[index].Label}");
    }

    Console.Write($"Select an option [1-{options.Count}]: ");
    var input = Console.ReadLine();
    return int.TryParse(input, out var selected) && selected >= 1 && selected <= options.Count
        ? options[selected - 1]
        : null;
}

static Task SaveInteractiveTrustDecisionAsync(string workspaceRoot, ProjectTrustStore trustStore)
{
    var options = ProjectTrustResolver.GetOptions(workspaceRoot, includeSessionOnly: false);
    var selected = SelectProjectTrustOption(options);
    if (selected is null)
    {
        Console.WriteLine("No trust decision saved.");
        return Task.CompletedTask;
    }

    trustStore.SetMany(selected.Updates);
    Console.WriteLine("Trust decision saved. Restart PiSharp for project resources to be reloaded.");
    return Task.CompletedTask;
}

static void PrintInteractiveHelp()
{
    Console.WriteLine("""
        Commands:
          /session                 Show current session metadata
          /model [ref|next|prev]   List models, select one, or cycle (--persist saves the default)
          /thinking [level|next]   List thinking levels, set one, or cycle (--persist saves the default)
          /name [text]             Show or set the session display name
          /label <entry-id> [text] Set or clear a bookmark label on an entry
          /stats                   Show session message/tool statistics
          /tree                    Show the turn tree (* marks active turn, [text] shows its label)
          /goto <entry-id|root> [--summarize]  Move point and optionally summarize abandoned work
          /compact [instructions]  Summarize old context and persist a compaction boundary
          /fork [turn-id]          Copy an active path into a new session
          /clone                   Clone the current active branch into a new session
          /new                     Start a new persistent session
          /resume                  Pick and resume a saved session
          /context                 List loaded AGENTS.md/CLAUDE.md files
          /trust                   Save project trust for the next startup
          /settings [key value]    Show settings status or set a global setting ("unset" clears)
          /reload                  Reload settings and keybindings
          /steer <text>            Queue input before the next model call
          /follow-up <text>       Queue input after the active run
          /exit, /quit             Exit

        While a turn is running, normal input steers the next model call.
        Use /follow-up <text> to wait until the current run would finish.
        Ctrl+C aborts the active turn and preserves queued messages.
        """);
}

static void PrintHelp()
{
    Console.WriteLine("""
        PiSharp - experimental C# port of Pi's coding-agent concepts using Microsoft Agent Framework

        Usage:
          pisharp [options] [@files...] [prompt...]

        Options:
          --model <name>              Model name (or PISHARP_MODEL), e.g. openai/gpt-5.5
          --models <pattern>          Model scope pattern for /model cycling (repeatable)
          --thinking <level>          Initial thinking level (or PISHARP_THINKING)
          --endpoint <url>            OpenAI-compatible base URL, e.g. http://localhost:8000/v1
          --api-key <key>             API key; defaults to PISHARP_API_KEY then OPENAI_API_KEY
          --offline                   Do not refresh remote model catalogs at startup
          --list-models               Print the model catalogue and exit
          --cwd <path>                Workspace root; defaults to current directory
          --context-root <path>       Stop parent AGENTS.md/CLAUDE.md discovery at this directory
          --extension, -e <path>      Load a trusted .NET extension DLL/directory (repeatable)
          --skill <path>              Load a skill file/directory (repeatable)
          --prompt-template <path>    Load a prompt template file/directory (repeatable)
          --mode <text|json|rpc>      Select text, JSON event, or JSON-RPC output
          --print, -p                 Run one prompt and exit
          --read-only                 Expose only read/search tools
          --no-tools, -nt              Disable built-in tools
          --no-auto-retry              Disable transient provider retries
          --no-extensions, -ne        Disable default extension discovery
          --no-skills, -ns            Disable default skill discovery
          --no-prompt-templates, -np  Disable default prompt discovery
          --approve, -a                Trust project-local resources for this run
          --no-approve, -na            Ignore project-local resources for this run
          --context-tokens <n>        Context window used by Pi-native compaction (default 128000)
          --max-output-tokens <n>     Maximum output tokens (default 16384)
          -c, --continue              Continue the most recently modified session for this workspace
          -r, --resume                Interactively select a saved workspace session
          --session <id|path>         Resume a session by id prefix or JSONL path
          --name <text>               Set the session display name
          --session-dir <path>        Override session storage root (PI_CODING_AGENT_SESSION_DIR / sessionDir setting)
          --no-session                Do not persist session state
          -h, --help                  Show help

        Examples:
          PISHARP_MODEL=gpt-5.4 pisharp "fix the failing tests"
          pisharp --list-models
          pisharp --model Qwen3.8-27B --endpoint http://192.168.0.97:8000/v1 "inspect this repo"
          pisharp --context-root . --model Qwen3.8-27B --endpoint http://localhost:8000/v1
          pisharp --continue
        """);
}
