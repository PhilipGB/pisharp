using System.Text.Json;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeInputBash;

public sealed class InputBashExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddInputHandler((eventData, _, _) =>
        {
            WriteRecord(new
            {
                type = "input",
                handler = "first",
                source = eventData.Source.ToString(),
                text = eventData.Text,
                behavior = eventData.StreamingBehavior?.ToString()
            });
            if (eventData.Text == "native-package-input-error")
                throw new InvalidOperationException("native-package-input-error");
            return ValueTask.FromResult<PiSharpInputResult?>(eventData.Text == "native-package-transform"
                ? PiSharpInputResult.Transform("native-package-stage-one")
                : PiSharpInputResult.Continue);
        });

        builder.AddInputHandler((eventData, context, _) =>
        {
            WriteRecord(new
            {
                type = "input",
                handler = "second",
                source = eventData.Source.ToString(),
                text = eventData.Text,
                behavior = eventData.StreamingBehavior?.ToString()
            });
            if (eventData.Text == "native-package-handled")
            {
                if (context.Mode == "tui") context.Ui.Notify("native-package-handled-notification");
                return ValueTask.FromResult<PiSharpInputResult?>(PiSharpInputResult.Handled);
            }
            return ValueTask.FromResult<PiSharpInputResult?>(eventData.Text == "native-package-stage-one"
                ? PiSharpInputResult.Transform("native-package-final")
                : PiSharpInputResult.Continue);
        });

        builder.AddUserBashHandler((eventData, _, _) =>
        {
            WriteRecord(new
            {
                type = "user_bash",
                command = eventData.Command,
                cwd = eventData.WorkingDirectory,
                excludeFromContext = eventData.ExcludeFromContext
            });
            if (eventData.Command.StartsWith("throw ", StringComparison.Ordinal))
                throw new InvalidOperationException("native-package-bash-error");
            if (eventData.Command.StartsWith("remote ", StringComparison.Ordinal))
            {
                return ValueTask.FromResult<PiSharpUserBashResult?>(PiSharpUserBashResult.Custom(
                    async (command, workingDirectory, output, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        await output(System.Text.Encoding.UTF8.GetBytes("custom-start\n"));
                        await output(System.Text.Encoding.UTF8.GetBytes($"{workingDirectory}:{command}\n"));
                        return 23;
                    }));
            }
            return ValueTask.FromResult<PiSharpUserBashResult?>(eventData.Command.StartsWith("touch ",
                StringComparison.Ordinal)
                ? PiSharpUserBashResult.Full("external-package-bash-result", 17, truncated: true)
                : null);
        });
    }

    private static void WriteRecord<T>(T record)
    {
        var path = Environment.GetEnvironmentVariable("PISHARP_NATIVE_INPUT_BASH_LOG");
        if (!string.IsNullOrWhiteSpace(path))
            File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine);
    }
}
