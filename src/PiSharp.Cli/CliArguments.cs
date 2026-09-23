namespace PiSharp.Cli;

public sealed record CliArguments(bool Help, bool Local, bool Print, bool Continue, bool NoSession, string? SessionPath, string Prompt,
    IReadOnlyList<string>? Tools, IReadOnlyList<string>? ExcludeTools, bool NoTools)
{
    private static IReadOnlyList<string> ParseToolNames(string[] arguments, ref int index, string flag)
    {
        if (++index >= arguments.Length || arguments[index].StartsWith('-')) throw new ArgumentException($"{flag} requires a comma-separated list.");
        return arguments[index].Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
    }

    public static CliArguments Parse(string[] arguments)
    {
        bool help = false, local = false, print = false, resume = false, noSession = false, noTools = false, afterSeparator = false;
        string? sessionPath = null;
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
                case "--print": print = true; break;
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
        return new CliArguments(help, local, print, resume, noSession, sessionPath, string.Join(" ", prompt), tools, excludeTools, noTools);
    }
}
