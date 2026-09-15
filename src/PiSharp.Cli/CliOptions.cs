namespace PiSharp.Cli;

internal enum OutputMode
{
    Text,
    Json,
    Rpc,
}

internal sealed record CliOptions(
    string WorkingDirectory,
    string Model,
    string? Endpoint,
    string ApiKey,
    int ContextTokens,
    int MaxOutputTokens,
    string? Prompt,
    IReadOnlyList<string> FilePaths,
    bool ShowHelp,
    bool ContinueSession,
    bool ResumeSession,
    string? SessionSelector,
    string? SessionDirectory,
    bool NoSession,
    string? ContextRoot,
    IReadOnlyList<string> ExtensionPaths,
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> PromptTemplatePaths,
    bool NoExtensions,
    bool NoSkills,
    bool NoPromptTemplates,
    OutputMode OutputMode,
    bool PrintMode,
    bool ReadOnly,
    bool NoTools,
    bool AutoRetry)
{
    public static CliOptions Parse(string[] args)
    {
        var cwd = Directory.GetCurrentDirectory();
        var model = Environment.GetEnvironmentVariable("PISHARP_MODEL") ?? string.Empty;
        var endpoint = Environment.GetEnvironmentVariable("PISHARP_ENDPOINT");
        var apiKey = Environment.GetEnvironmentVariable("PISHARP_API_KEY")
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? string.Empty;
        var contextTokens = ParsePositiveInt(Environment.GetEnvironmentVariable("PISHARP_CONTEXT_TOKENS"), 128_000);
        var maxOutputTokens = ParsePositiveInt(Environment.GetEnvironmentVariable("PISHARP_MAX_OUTPUT_TOKENS"), 16_384);
        var promptParts = new List<string>();
        var filePaths = new List<string>();
        var showHelp = false;
        var continueSession = false;
        var resumeSession = false;
        string? sessionSelector = null;
        string? sessionDirectory = Environment.GetEnvironmentVariable("PISHARP_SESSION_DIR");
        var noSession = false;
        string? contextRoot = null;
        var extensionPaths = new List<string>();
        var skillPaths = new List<string>();
        var promptTemplatePaths = new List<string>();
        var noExtensions = false;
        var noSkills = false;
        var noPromptTemplates = false;
        var outputMode = OutputMode.Text;
        var printMode = false;
        var readOnly = false;
        var noTools = false;
        var autoRetry = true;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    showHelp = true;
                    break;
                case "--cwd":
                    cwd = RequireValue(args, ref i, "--cwd");
                    break;
                case "--model":
                    model = RequireValue(args, ref i, "--model");
                    break;
                case "--endpoint":
                    endpoint = RequireValue(args, ref i, "--endpoint");
                    break;
                case "--api-key":
                    apiKey = RequireValue(args, ref i, "--api-key");
                    break;
                case "--context-tokens":
                    contextTokens = ParsePositiveInt(RequireValue(args, ref i, "--context-tokens"), contextTokens);
                    break;
                case "--max-output-tokens":
                    maxOutputTokens = ParsePositiveInt(RequireValue(args, ref i, "--max-output-tokens"), maxOutputTokens);
                    break;
                case "-c" or "--continue":
                    continueSession = true;
                    break;
                case "-r" or "--resume":
                    resumeSession = true;
                    break;
                case "--session":
                    sessionSelector = RequireValue(args, ref i, "--session");
                    break;
                case "--session-dir":
                    sessionDirectory = RequireValue(args, ref i, "--session-dir");
                    break;
                case "--no-session":
                    noSession = true;
                    break;
                case "--context-root":
                    contextRoot = RequireValue(args, ref i, "--context-root");
                    break;
                case "--extension" or "-e":
                    extensionPaths.Add(RequireValue(args, ref i, "--extension"));
                    break;
                case "--skill":
                    skillPaths.Add(RequireValue(args, ref i, "--skill"));
                    break;
                case "--prompt-template":
                    promptTemplatePaths.Add(RequireValue(args, ref i, "--prompt-template"));
                    break;
                case "--mode":
                    outputMode = ParseOutputMode(RequireValue(args, ref i, "--mode"));
                    break;
                case "--print" or "-p":
                    printMode = true;
                    break;
                case "--read-only":
                    readOnly = true;
                    break;
                case "--no-tools" or "-nt":
                    noTools = true;
                    break;
                case "--no-auto-retry":
                    autoRetry = false;
                    break;
                case "--no-extensions" or "-ne":
                    noExtensions = true;
                    break;
                case "--no-skills" or "-ns":
                    noSkills = true;
                    break;
                case "--no-prompt-templates" or "-np":
                    noPromptTemplates = true;
                    break;
                case "--":
                    AddPositionalArguments(args[(i + 1)..], promptParts, filePaths);
                    i = args.Length;
                    break;
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {args[i]}");
                    }
                    AddPositionalArguments([args[i]], promptParts, filePaths);
                    break;
            }
        }

        if (outputMode == OutputMode.Rpc && filePaths.Count > 0)
        {
            throw new ArgumentException("@file arguments are not supported in RPC mode.");
        }

        if (!showHelp && string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model is required. Set PISHARP_MODEL or pass --model <name>.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                apiKey = "unused";
            }
            else if (!showHelp)
            {
                throw new ArgumentException("An API key is required. Set PISHARP_API_KEY/OPENAI_API_KEY or pass --api-key.");
            }
        }

        var sessionModes = (continueSession ? 1 : 0) + (resumeSession ? 1 : 0) + (sessionSelector is null ? 0 : 1) + (noSession ? 1 : 0);
        if (sessionModes > 1)
        {
            throw new ArgumentException("Use only one of --continue, --resume, --session, or --no-session.");
        }

        var fullCwd = Path.GetFullPath(cwd);
        if (contextRoot is not null)
        {
            contextRoot = Path.GetFullPath(contextRoot, fullCwd);
        }

        return new CliOptions(
            fullCwd,
            model,
            endpoint,
            apiKey,
            contextTokens,
            maxOutputTokens,
            promptParts.Count == 0 ? null : string.Join(' ', promptParts),
            filePaths,
            showHelp,
            continueSession,
            resumeSession,
            sessionSelector,
            sessionDirectory,
            noSession,
            contextRoot,
            extensionPaths,
            skillPaths,
            promptTemplatePaths,
            noExtensions,
            noSkills,
            noPromptTemplates,
            outputMode,
            printMode,
            readOnly,
            noTools,
            autoRetry);
    }

    private static void AddPositionalArguments(
        IEnumerable<string> arguments,
        ICollection<string> promptParts,
        ICollection<string> filePaths)
    {
        foreach (var argument in arguments)
        {
            if (argument.StartsWith('@') && argument.Length > 1)
            {
                filePaths.Add(argument[1..]);
            }
            else
            {
                promptParts.Add(argument);
            }
        }
    }

    private static OutputMode ParseOutputMode(string value) => value.ToLowerInvariant() switch
    {
        "text" => OutputMode.Text,
        "json" => OutputMode.Json,
        "rpc" => OutputMode.Rpc,
        _ => throw new ArgumentException($"Unknown output mode '{value}'. Use text, json, or rpc."),
    };

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return args[index];
    }

    private static int ParsePositiveInt(string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"Expected a positive integer but received '{value}'.");
        }

        return parsed;
    }
}
