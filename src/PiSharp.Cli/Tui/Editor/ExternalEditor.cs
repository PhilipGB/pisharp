using System.Diagnostics;
using System.Text;

namespace PiSharp.Cli.Tui;

internal sealed record ExternalEditorResult(bool Success, string Content, int? ExitCode = null);

/// <summary>Runs a configured editor on a private temporary prompt file without a shell.</summary>
internal static class ExternalEditor
{
    public static string ResolveCommand(string? configured, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
        var visual = environment("VISUAL");
        if (!string.IsNullOrWhiteSpace(visual)) return visual.Trim();
        var editor = environment("EDITOR");
        if (!string.IsNullOrWhiteSpace(editor)) return editor.Trim();
        return OperatingSystem.IsWindows() ? "notepad" : "nano";
    }

    public static async Task<ExternalEditorResult> EditAsync(string content, string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var arguments = ParseCommand(command);
        if (arguments.Count == 0) throw new ArgumentException("The external editor command is empty.", nameof(command));

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-editor-{Guid.NewGuid():N}");
        var temporaryPath = Path.Combine(temporaryDirectory, "prompt.md");
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(temporaryDirectory);
            else Directory.CreateDirectory(temporaryDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporaryPath, fileOptions))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            var start = new ProcessStartInfo(arguments[0]) { UseShellExecute = false };
            foreach (var argument in arguments.Skip(1)) start.ArgumentList.Add(argument);
            start.ArgumentList.Add(temporaryPath);
            using var process = Process.Start(start) ?? throw new IOException("The external editor process did not start.");
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) return new(false, content, process.ExitCode);

            var edited = (await File.ReadAllTextAsync(temporaryPath, cancellationToken)).TrimStart('\uFEFF');
            if (edited.EndsWith("\r\n", StringComparison.Ordinal)) edited = edited[..^2];
            else if (edited.EndsWith('\n') || edited.EndsWith('\r')) edited = edited[..^1];
            var safe = new string(edited.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                .Where(character => character is '\n' or '\t' || !char.IsControl(character)).ToArray());
            return new(true, safe);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch { }
        }
    }

    private static IReadOnlyList<string> ParseCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length > 4096 || command.Any(char.IsControl))
            throw new ArgumentException("The external editor command is invalid or too long.", nameof(command));

        var arguments = new List<string>();
        var value = new StringBuilder();
        var quote = '\0';
        var started = false;
        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (character == '\\' && quote != '\'')
            {
                if (index + 1 < command.Length &&
                    (command[index + 1] == quote || command[index + 1] is '\\' or '"' or '\'' || char.IsWhiteSpace(command[index + 1])))
                {
                    value.Append(command[++index]);
                    started = true;
                }
                else
                {
                    value.Append(character);
                    started = true;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else value.Append(character);
                started = true;
                continue;
            }
            if (character is '"' or '\'')
            {
                quote = character;
                started = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (started)
                {
                    arguments.Add(value.ToString());
                    value.Clear();
                    started = false;
                }
            }
            else
            {
                value.Append(character);
                started = true;
            }
        }
        if (quote != '\0') throw new ArgumentException("The external editor command has an unmatched quote.", nameof(command));
        if (started) arguments.Add(value.ToString());
        return arguments;
    }
}
