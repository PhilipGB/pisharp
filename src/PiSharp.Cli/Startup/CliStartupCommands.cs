namespace PiSharp.Cli.Startup;

using PiSharp.Cli.Packages;

internal static class CliStartupCommands
{
    public static async Task<CliArguments?> ParseOrRunAsync(string[] args, string agentDirectory,
        TextWriter output, TextWriter error)
    {
        if (args.Length > 0 && args[0] == "auth")
        {
            Environment.ExitCode = await AuthStatusCommand.RunAsync(args[1..], agentDirectory,
                Environment.GetEnvironmentVariable, output, error);
            return null;
        }
        if (args.Length > 0 && args[0] == "mcp")
        {
            Environment.ExitCode = await McpCommand.RunAsync(args[1..], agentDirectory, Environment.CurrentDirectory,
                output, error);
            return null;
        }
        if (args.Length > 0 && args[0] == "--export")
        {
            Environment.ExitCode = await SessionExportCommand.RunAsync(args[1..], output, error);
            return null;
        }
        if (await ResourcePackageCommand.TryRunAsync(args, agentDirectory, Environment.CurrentDirectory,
                Console.In, output, error, interactive: !Console.IsInputRedirected && !Console.IsOutputRedirected))
            return null;
        CliArguments cli;
        string? pendingUnknownOption = null;
        try { _ = CliArguments.Parse(args); }
        catch (ArgumentException parseError) when (parseError.Message.StartsWith("Unknown option:", StringComparison.Ordinal))
        {
            pendingUnknownOption = parseError.Message;
        }
        catch (ArgumentException)
        {
            // Extension discovery validates the complete argument set after registrations are available.
        }
        try { cli = CliArguments.ParseForExtensionDiscovery(args) with { PendingUnknownOption = pendingUnknownOption }; }
        catch (ArgumentException e)
        {
            error.WriteLine(e.Message);
            Environment.ExitCode = 2;
            return null;
        }
        if (cli.PendingUnknownOption is { } unknownOption &&
            (cli.Help || cli.Version || cli.Provider is not null && cli.ModelOverride is null && !cli.ListModels))
        {
            error.WriteLine(unknownOption);
            Environment.ExitCode = 2;
            return null;
        }
        if (cli.Version)
        {
            output.WriteLine(typeof(CliArguments).Assembly.GetName().Version?.ToString(3) ?? "unknown");
            return null;
        }
        if (cli.Help)
        {
            output.WriteLine("PiSharp (incomplete implementation)\nUsage: pisharp [--local] [--provider <id> --model <id>] [--model <id>] [--models <globs>] [--thinking <level>] [--api-key <key>] [--list-models [pattern]] [--system-prompt <text|file>] [--append-system-prompt <text|file>] [--no-context-files] [--no-extensions] [-e|--extension <path>] [--no-skills] [--skill <path>] [--no-prompt-templates] [--prompt-template <path>] [--theme <path>] [--use-theme <name[/name]>] [--no-themes] [--offline] [--verbose] [-a|--approve|-na|--no-approve] [--mode text|interactive|print|json|rpc] [-p|--print] [-h|--help] [-v|--version] [-c|--continue | --session <path|project-id> | --fork <path|project-id> | --no-session] [--session-dir <dir>] [--name <label>] [prompt] [@files...]\n--provider <id> filters explicit model selection and requires --model <id>.\n--tools <read,bash,edit,write,grep,find,ls> selects tools (grep/find/ls are opt-in); --exclude-tools <names> removes tools; --no-tools disables defaults (including extension tools); --no-builtin-tools disables only default built-ins.\nProviders and static model metadata may be configured in $PISHARP_AGENT_DIR/models.json. Credentials are read from environment or private auth.json; --api-key is runtime-only.\nOffline credential status: pisharp auth check --provider <id> [--model <configured-exact-id>] [--local]; explicit pisharp auth print-api-key --provider <id> prints an API key to stdout.\nMCP configuration/status: pisharp mcp list [--json], pisharp mcp add, pisharp mcp remove, pisharp mcp login, pisharp mcp logout.\nStandalone private HTML: pisharp --export <PiSharp-session-file> [output.html]; never overwrites.\n--local uses http://192.168.0.97:8000/v1 and Qwen3.8-27B-GGUF (no API key required).\nInteractive: /model, /models, /llama, /settings, /thinking, /scoped-models, /login, /logout, /tree, /branch, /fork, /clone, /new, /sessions, /resume, /delete-session, /compact, /copy, /export, /export-jsonl, /import, /name, /session, /trust, /reload, /hotkeys, /quit.");
            return null;
        }
        if (cli.Provider is not null && cli.ModelOverride is null && !cli.ListModels)
        {
            error.WriteLine($"--provider requires --model (for example: --provider {cli.Provider} --model <pattern>).");
            Environment.ExitCode = 2;
            return null;
        }
        return cli;
    }
    public static async Task ListModelsAsync(ProviderModelRuntime models, CliArguments cli, TextWriter output)
    {
        var catalog = await models.ListModelsAsync(cli.Provider);
        var matching = catalog.Where(model => CliModelFilter.Matches(cli.ListModelsFilter, model.Provider, model.Id))
            .OrderBy(model => model.Provider, StringComparer.Ordinal).ThenBy(model => model.Id, StringComparer.Ordinal).ToArray();
        if (cli.ListModelsFilter is not null && matching.Length == 0)
            output.WriteLine($"No models matching \"{cli.ListModelsFilter}\"");
        foreach (var model in matching)
            output.WriteLine($"{model.Provider}/{model.Id}\t{(model.Available ? model.Status ?? "available" : model.UnavailableReason ?? "unavailable")}\t{model.ContextLength?.ToString() ?? ""}");
    }
}
