using PiSharp.Cli.Tui;

namespace PiSharp.Cli.Startup;

internal static class PrintPromptMode
{
    public static async Task RunAsync(CliArguments cli, string stdinContent, string workingDirectory, TerminalConversationExecution execution)
    {
        CliFileArguments.PromptFiles promptFiles;
        try { promptFiles = await CliFileArguments.ProcessFilesAsync(cli.Prompt, cli.FileArguments, workingDirectory); }
        catch (Exception e) when (e is IOException or ArgumentException) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; return; }
        promptFiles = promptFiles with { Text = stdinContent + promptFiles.Text };
        if (string.IsNullOrWhiteSpace(promptFiles.Text) && promptFiles.Images.Count == 0) { Console.Error.WriteLine("A prompt is required in print mode."); Environment.ExitCode = 2; }
        else await execution.RunAsync(promptFiles.Text, promptFiles.Images);
    }
}
