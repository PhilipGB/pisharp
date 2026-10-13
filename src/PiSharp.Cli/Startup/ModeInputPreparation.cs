namespace PiSharp.Cli.Startup;

internal sealed record PreparedModeInput(bool Print, string Stdin);

internal static class ModeInputPreparation
{
    public static async Task<PreparedModeInput?> PrepareAsync(CliArguments cli, ModelSelection selection, TextReader standardInput)
    {
        bool print = cli.Print || cli.Mode == "print" || Console.IsInputRedirected || Console.IsOutputRedirected;
        var prompt = cli.Prompt;
        // Pi combines trimmed piped input before @file content and the positional prompt.
        // RPC owns stdin as a command stream and must not consume it here.
        string stdinContent;
        try { stdinContent = cli.Mode != "rpc" && Console.IsInputRedirected ? await CliStdin.ReadAsync(standardInput) : ""; }
        catch (InvalidDataException error)
        {
            Console.Error.WriteLine(error.Message);
            Environment.ExitCode = 2;
            return null;
        }
        if (!selection.Authenticated && cli.Mode != "rpc" &&
            (print || cli.Mode == "json" || !string.IsNullOrWhiteSpace(prompt)))
        {
            Console.Error.WriteLine($"Provider '{selection.Provider.Id}' is not authenticated. Use /login {selection.Provider.Id} in an interactive terminal or configure {selection.Provider.ApiKeyEnvironment ?? "a credential"}.");
            Environment.ExitCode = 2;
            return null;
        }
        return new(print, stdinContent);
    }
}
