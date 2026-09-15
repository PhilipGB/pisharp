using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    var bootstrap = await AgentFactory.CreateAsync(options, shutdown.Token);
    var agent = bootstrap.Agent;
    var sessions = await SessionController.CreateAsync(agent, options, shutdown.Token);

    if (options.Prompt is not null)
    {
        var response = await RunTurnAsync(agent, sessions.Session, options.Prompt, shutdown.Token);
        await sessions.PersistTurnAsync(options.Prompt, response, shutdown.Token);
        return 0;
    }

    Console.WriteLine($"PiSharp  |  {options.Model}  |  {options.WorkingDirectory}");
    Console.WriteLine(sessions.FormatSessionInfo());
    if (bootstrap.ContextFiles.Count > 0)
    {
        Console.WriteLine($"Context files: {bootstrap.ContextFiles.Count} (use /context to list)");
    }
    Console.WriteLine("Type /help for commands. Ctrl+C cancels the process.");

    using var promptReader = new TerminalPromptReader(
        Console.In,
        Console.Out,
        enableBracketedPaste: !Console.IsInputRedirected && !Console.IsOutputRedirected);

    while (!shutdown.IsCancellationRequested)
    {
        Console.WriteLine();
        var input = promptReader.ReadPrompt();
        if (input is null || input.Equals("/exit", StringComparison.OrdinalIgnoreCase) || input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        if (!input.Contains('\n') && input.StartsWith("/", StringComparison.Ordinal))
        {
            if (await HandleCommandAsync(input, sessions, bootstrap.ContextFiles, shutdown.Token))
            {
                continue;
            }
        }

        var response = await RunTurnAsync(agent, sessions.Session, input, shutdown.Token);
        await sessions.PersistTurnAsync(input, response, shutdown.Token);
    }

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

static async Task<string> RunTurnAsync(
    AIAgent agent,
    AgentSession session,
    string prompt,
    CancellationToken cancellationToken)
{
    var response = new StringBuilder();
    await foreach (var update in agent.RunStreamingAsync(prompt, session, cancellationToken: cancellationToken))
    {
        if ((update.Role is null || update.Role == ChatRole.Assistant) && !string.IsNullOrEmpty(update.Text))
        {
            Console.Write(update.Text);
            response.Append(update.Text);
        }
    }

    Console.WriteLine();
    return response.ToString();
}

static async Task<bool> HandleCommandAsync(
    string input,
    SessionController sessions,
    IReadOnlyList<string> contextFiles,
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
        case "/exit":
        case "/quit":
            return false;
        default:
            // Unknown slash-prefixed input remains a normal user prompt for now.
            return false;
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
          /exit, /quit             Exit

        Multi-line terminal pastes are submitted as one prompt when the terminal supports bracketed paste.
        Slash commands are recognized only for single-line input.
        """);
}

static void PrintHelp()
{
    Console.WriteLine("""
        PiSharp - experimental C# port of Pi's coding-agent concepts using Microsoft Agent Framework

        Usage:
          pisharp [options] [prompt...]

        Options:
          --model <name>              Model name (or PISHARP_MODEL)
          --endpoint <url>            OpenAI-compatible base URL, e.g. http://localhost:8000/v1
          --api-key <key>             API key; defaults to PISHARP_API_KEY then OPENAI_API_KEY
          --cwd <path>                Workspace root; defaults to current directory
          --context-root <path>       Stop parent AGENTS.md/CLAUDE.md discovery at this directory
          --context-tokens <n>        Context window used by Harness compaction (default 128000)
          --max-output-tokens <n>     Maximum output tokens (default 16384)
          -c, --continue              Continue the most recently modified session for this workspace
          -r, --resume                Interactively select a saved session
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
