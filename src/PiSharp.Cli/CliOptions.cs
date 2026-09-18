using PiSharp.Core.Settings;

namespace PiSharp.Cli;

internal enum OutputMode
{
    Text,
    Json,
    Rpc,
}

internal sealed record CliOptions(
    string WorkingDirectory,
    string? Model,
    string? Endpoint,
    string? ApiKey,
    string? Provider,
    IReadOnlyList<string> Models,
    string? Thinking,
    bool ListModels,
    bool Offline,
    int ContextTokens,
    int MaxOutputTokens,
    bool ContextTokensExplicit,
    bool MaxOutputTokensExplicit,
    string? Prompt,
    IReadOnlyList<string> FilePaths,
    bool ShowHelp,
    bool ContinueSession,
    bool ResumeSession,
    string? SessionSelector,
    string? SessionName,
    string? SessionDirectory,
    bool NoSession,
    string? ContextRoot,
    IReadOnlyList<string> ExtensionPaths,
    IReadOnlyList<string> SkillPaths,
    IReadOnlyList<string> PromptTemplatePaths,
    bool NoExtensions,
    bool NoSkills,
    bool NoPromptTemplates,
    bool? ProjectTrustOverride,
    OutputMode OutputMode,
    bool PrintMode,
    bool ReadOnly,
    bool NoTools,
    bool AutoRetry)
{
    public static CliOptions Parse(string[] args)
    {
        var cwd = Directory.GetCurrentDirectory();
        // Compatibility env vars map into the model runtime resolution (PISHARP_MODEL is a
        // --model alias, PISHARP_ENDPOINT the local-endpoint workflow, the key env vars feed
        // the provider ambient auth). Model/key are no longer required up front: the runtime
        // resolves auth per provider (pinned Pi behavior).
        var model = Environment.GetEnvironmentVariable("PISHARP_MODEL");
        var endpoint = Environment.GetEnvironmentVariable("PISHARP_ENDPOINT");
        var apiKey = Environment.GetEnvironmentVariable("PISHARP_API_KEY")
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var provider = Environment.GetEnvironmentVariable("PISHARP_PROVIDER");
        var modelPatterns = new List<string>();
        var thinking = Environment.GetEnvironmentVariable("PISHARP_THINKING");
        var listModels = false;
        var offline = Environment.GetEnvironmentVariable("PI_OFFLINE") is "1" or "true" or "yes";
        // Explicit user intent (flag or env var) overrides model metadata; the bare defaults
        // let the model's own context window / max output win (pinned behavior).
        var contextTokensEnv = Environment.GetEnvironmentVariable("PISHARP_CONTEXT_TOKENS");
        var maxOutputTokensEnv = Environment.GetEnvironmentVariable("PISHARP_MAX_OUTPUT_TOKENS");
        var contextTokens = ParsePositiveInt(contextTokensEnv, 128_000);
        var maxOutputTokens = ParsePositiveInt(maxOutputTokensEnv, 16_384);
        var contextTokensExplicit = contextTokensEnv is not null;
        var maxOutputTokensExplicit = maxOutputTokensEnv is not null;
        var promptParts = new List<string>();
        var filePaths = new List<string>();
        var showHelp = false;
        var continueSession = false;
        var resumeSession = false;
        string? sessionSelector = null;
        string? sessionName = null;
        // Pi's session storage override env var (PI_CODING_AGENT_SESSION_DIR).
        string? sessionDirectory = Environment.GetEnvironmentVariable(SettingsPaths.SessionDirEnvironmentVariable);
        var noSession = false;
        string? contextRoot = null;
        var extensionPaths = new List<string>();
        var skillPaths = new List<string>();
        var promptTemplatePaths = new List<string>();
        var noExtensions = false;
        var noSkills = false;
        var noPromptTemplates = false;
        bool? projectTrustOverride = null;
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
                case "--provider":
                    provider = RequireValue(args, ref i, "--provider");
                    break;
                case "--models":
                    modelPatterns.Add(RequireValue(args, ref i, "--models"));
                    break;
                case "--thinking":
                    thinking = RequireValue(args, ref i, "--thinking");
                    break;
                case "--list-models":
                    listModels = true;
                    break;
                case "--offline":
                    offline = true;
                    break;
                case "--endpoint":
                    endpoint = RequireValue(args, ref i, "--endpoint");
                    break;
                case "--api-key":
                    apiKey = RequireValue(args, ref i, "--api-key");
                    break;
                case "--context-tokens":
                    contextTokens = ParsePositiveInt(RequireValue(args, ref i, "--context-tokens"), contextTokens);
                    contextTokensExplicit = true;
                    break;
                case "--max-output-tokens":
                    maxOutputTokens = ParsePositiveInt(RequireValue(args, ref i, "--max-output-tokens"), maxOutputTokens);
                    maxOutputTokensExplicit = true;
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
                case "--name":
                    sessionName = RequireValue(args, ref i, "--name");
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
                case "--approve" or "-a":
                    SetProjectTrustOverride(ref projectTrustOverride, true);
                    break;
                case "--no-approve" or "-na":
                    SetProjectTrustOverride(ref projectTrustOverride, false);
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
            provider,
            modelPatterns,
            thinking,
            listModels,
            offline,
            contextTokens,
            maxOutputTokens,
            contextTokensExplicit,
            maxOutputTokensExplicit,
            promptParts.Count == 0 ? null : string.Join(' ', promptParts),
            filePaths,
            showHelp,
            continueSession,
            resumeSession,
            sessionSelector,
            sessionName,
            sessionDirectory,
            noSession,
            contextRoot,
            extensionPaths,
            skillPaths,
            promptTemplatePaths,
            noExtensions,
            noSkills,
            noPromptTemplates,
            projectTrustOverride,
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

    private static void SetProjectTrustOverride(ref bool? current, bool value)
    {
        if (current is bool existing && existing != value)
        {
            throw new ArgumentException("--approve and --no-approve are mutually exclusive.");
        }
        current = value;
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
