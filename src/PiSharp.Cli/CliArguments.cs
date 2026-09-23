namespace PiSharp.Cli;

public sealed record CliArguments(bool Help, bool Local, bool Print, bool Continue, bool NoSession, string? SessionPath, string Prompt,
    IReadOnlyList<string>? Tools, IReadOnlyList<string>? ExcludeTools, bool NoTools, string Mode, bool? ProjectTrustOverride = null, string? SessionDirectory = null, bool ListModels = false, string? ModelOverride = null, string? SessionName = null)
{
    private static IReadOnlyList<string> ParseToolNames(string[] arguments, ref int index, string flag)
    {
        if (++index >= arguments.Length || arguments[index].StartsWith('-')) throw new ArgumentException($"{flag} requires a comma-separated list.");
        return arguments[index].Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
    }

    public static CliArguments Parse(string[] arguments)
    {
        bool help = false, local = false, print = false, resume = false, noSession = false, noTools = false, afterSeparator = false, listModels = false;
        bool? trust = null;
        string? sessionPath = null, sessionDirectory = null, modelOverride = null, sessionName = null;
        var mode = "interactive";
        IReadOnlyList<string>? tools = null, excludeTools = null;
        var prompt = new List<string>();
        for (var i = 0; i < arguments.Length; i++)
        {
            var arg = arguments[i];
            if (!afterSeparator && arg == "--") { afterSeparator = true; continue; }
            if (afterSeparator) { prompt.Add(arg); continue; }
            switch (arg)
            {
                case "--help": help = true; break;
                case "--local": local = true; break;
                case "--approve":
                    if (trust == false) throw new ArgumentException("--approve conflicts with --no-approve.");
                    trust = true;
                    break;
                case "--no-approve":
                    if (trust == true) throw new ArgumentException("--no-approve conflicts with --approve.");
                    trust = false;
                    break;
                case "--print": print = true; break;
                case "--list-models": listModels = true; break;
                case "--continue": resume = true; break;
                case "--no-session": noSession = true; break;
                case "--no-tools": case "-nt": noTools = true; break;
                case "--tools":
                case "-t":
                    tools = ParseToolNames(arguments, ref i, arg);
                    break;
                case "--exclude-tools":
                case "-xt":
                    excludeTools = ParseToolNames(arguments, ref i, arg);
                    break;
                case "--mode":
                    if (++i >= arguments.Length || arguments[i] is not ("json" or "print" or "interactive" or "rpc"))
                        throw new ArgumentException("--mode supports interactive, print, json, or rpc.");
                    mode = arguments[i];
                    break;
                case "--model":
                    if (++i >= arguments.Length || string.IsNullOrWhiteSpace(arguments[i]) || arguments[i].StartsWith('-'))
                        throw new ArgumentException("--model requires a model ID.");
                    modelOverride = arguments[i];
                    break;
                case "--name":
                case "-n":
                    if (++i >= arguments.Length || string.IsNullOrWhiteSpace(arguments[i]) || arguments[i].StartsWith('-'))
                        throw new ArgumentException("--name requires a nonempty session name.");
                    sessionName = arguments[i];
                    break;
                case "--session-dir":
                    if (++i >= arguments.Length || arguments[i].StartsWith('-'))
                        throw new ArgumentException("--session-dir requires a directory path.");
                    sessionDirectory = arguments[i];
                    break;
                case "--session":
                    if (++i >= arguments.Length || arguments[i].StartsWith('-'))
                        throw new ArgumentException("--session requires a file path.");
                    sessionPath = arguments[i];
                    break;
                default:
                    if (arg.StartsWith('-')) throw new ArgumentException($"Unknown option: {arg}");
                    prompt.Add(arg);
                    break;
            }
        }
        if ((resume ? 1 : 0) + (noSession ? 1 : 0) + (sessionPath is null ? 0 : 1) > 1)
            throw new ArgumentException("--continue, --session and --no-session cannot be combined.");
        if (print && mode is not "interactive" and not "print") throw new ArgumentException("--print cannot be combined with --mode json or rpc.");
        if (mode == "rpc" && prompt.Count > 0) throw new ArgumentException("RPC mode reads commands from stdin, not positional prompts.");
        if (listModels && (prompt.Count > 0 || mode != "interactive" || print || resume || sessionPath is not null || noSession || sessionName is not null))
            throw new ArgumentException("--list-models cannot be combined with a prompt, mode or session operation.");
        return new CliArguments(help, local, print, resume, noSession, sessionPath, string.Join(" ", prompt), tools, excludeTools, noTools, mode, trust, sessionDirectory, listModels, modelOverride, sessionName);
    }
}
