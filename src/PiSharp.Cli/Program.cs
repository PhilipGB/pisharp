using PiSharp.Core;
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

    var bootstrap = await AgentFactory.CreateAsync(options, shutdown.Token);
    liveTurns = new LiveTurnCoordinator(bootstrap.TurnQueue);
    var sessions = await SessionController.CreateAsync(bootstrap.Agent, options, shutdown.Token);

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
        var result = await AgentTurnRunner.RunAsync(
            bootstrap,
            sessions,
            liveTurns,
            promptInput.Text,
            options.WorkingDirectory,
            new TerminalChatOutput(),
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

    Console.WriteLine($"PiSharp  |  {options.Model}  |  {options.WorkingDirectory}");
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
    await RunInteractiveAsync(
        bootstrap,
        sessions,
        options.WorkingDirectory,
        liveTurns,
        new TerminalChatOutput(),
        promptReader,
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
                bootstrap.ContextFiles,
                bootstrap.ExtensionHost,
                workspaceRoot,
                liveTurns.Queue,
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
        var result = await activeTurn;
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
    IReadOnlyList<string> contextFiles,
    PiSharpExtensionHost extensionHost,
    string workspaceRoot,
    TurnMessageQueue turnQueue,
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
        case "/session":
            Console.WriteLine(sessions.FormatSessionInfo());
            return true;
        case "/tree":
            Console.WriteLine(sessions.FormatTree());
            return true;
        case "/goto":
            if (string.IsNullOrWhiteSpace(argument))
            {
                Console.WriteLine("Usage: /goto <turn-id|root>");
                return true;
            }
            await sessions.CheckoutAsync(argument, cancellationToken);
            Console.WriteLine($"Checked out {argument}. The next prompt will branch from this point.");
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

static void PrintInteractiveHelp()
{
    Console.WriteLine("""
        Commands:
          /session                 Show current session metadata
          /tree                    Show the turn tree (* marks active turn)
          /goto <turn-id|root>     Move the active point; next prompt creates a branch
          /fork [turn-id]          Copy an active path into a new session
          /clone                   Clone the current active branch into a new session
          /new                     Start a new persistent session
          /resume                  Pick and resume a saved session
          /context                 List loaded AGENTS.md/CLAUDE.md files
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
          --model <name>              Model name (or PISHARP_MODEL)
          --endpoint <url>            OpenAI-compatible base URL, e.g. http://localhost:8000/v1
          --api-key <key>             API key; defaults to PISHARP_API_KEY then OPENAI_API_KEY
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
          --context-tokens <n>        Context window used by Harness compaction (default 128000)
          --max-output-tokens <n>     Maximum output tokens (default 16384)
          -c, --continue              Continue the most recently modified session for this workspace
          -r, --resume                Interactively select a saved workspace session
          --session <id|path>         Resume a session by id prefix or JSONL path
          --session-dir <path>        Override ~/.pisharp/sessions storage root
          --no-session                Do not persist session state
          -h, --help                  Show help

        Examples:
          PISHARP_MODEL=gpt-5.4 pisharp "fix the failing tests"
          pisharp --model Qwen3.8-27B --endpoint http://192.168.0.97:8000/v1 "inspect this repo"
          pisharp --context-root . --model Qwen3.8-27B --endpoint http://localhost:8000/v1
          pisharp --continue
        """);
}
