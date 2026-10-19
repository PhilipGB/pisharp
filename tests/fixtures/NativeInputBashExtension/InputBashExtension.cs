using System.Text.Json;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeInputBash;

public sealed class InputBashExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddCommand("native-send", async (arguments, context, cancellationToken) =>
        {
            var message = arguments.Trim() switch
            {
                "idle" => "native-package-extension-idle",
                "active" => "native-package-extension-active",
                _ => throw new ArgumentException("Expected 'idle' or 'active'.", nameof(arguments))
            };
            await context.SendUserMessageAsync(message, cancellationToken);
            return "sent";
        }, description: "Send a user message through the native input dispatcher.");

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
            if (Environment.GetEnvironmentVariable("PISHARP_NATIVE_INPUT_BASH_FAIL_CONFIGURE") == "true")
            {
                WriteRecord(new { type = "rollback_input", text = eventData.Text });
                return ValueTask.FromResult<PiSharpInputResult?>(PiSharpInputResult.Handled);
            }
            return ValueTask.FromResult<PiSharpInputResult?>(eventData.Text switch
            {
                "native-package-transform" => PiSharpInputResult.Transform("native-package-stage-one"),
                "native-package-extension-idle" => PiSharpInputResult.Transform("native-package-extension-idle-stage-one"),
                "native-package-extension-active" => PiSharpInputResult.Transform("native-package-extension-active-stage-one"),
                "native-package-extension-idle-stage-one" => PiSharpInputResult.Transform("native-package-extension-idle-final"),
                "native-package-extension-active-stage-one" => PiSharpInputResult.Transform("native-package-extension-active-final"),
                _ => PiSharpInputResult.Continue
            });
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
            return ValueTask.FromResult<PiSharpInputResult?>(eventData.Text switch
            {
                "native-package-stage-one" => PiSharpInputResult.Transform("native-package-final"),
                "native-package-extension-idle-stage-one" => PiSharpInputResult.Transform("native-package-extension-idle-final"),
                "native-package-extension-active-stage-one" => PiSharpInputResult.Transform("native-package-extension-active-final"),
                _ => PiSharpInputResult.Continue
            });
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
            if (Environment.GetEnvironmentVariable("PISHARP_NATIVE_INPUT_BASH_FAIL_CONFIGURE") == "true")
            {
                WriteRecord(new { type = "rollback_bash", command = eventData.Command });
                return ValueTask.FromResult<PiSharpUserBashResult?>(PiSharpUserBashResult.Full("rollback-result"));
            }
            if (eventData.Command.StartsWith("cancel-result ", StringComparison.Ordinal))
                return ValueTask.FromResult<PiSharpUserBashResult?>(PiSharpUserBashResult.Full(
                    "external-package-cancelled-result", cancelled: true, fullOutputPath: "external-full-output.log"));
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

        if (Environment.GetEnvironmentVariable("PISHARP_NATIVE_INPUT_BASH_FAIL_CONFIGURE") == "true")
        {
            WriteRecord(new { type = "configure_failure" });
            throw new InvalidOperationException("native input-bash fixture Configure failure");
        }
    }

    private static void WriteRecord<T>(T record)
    {
        var path = Environment.GetEnvironmentVariable("PISHARP_NATIVE_INPUT_BASH_LOG");
        if (!string.IsNullOrWhiteSpace(path))
            File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine);
    }
}
