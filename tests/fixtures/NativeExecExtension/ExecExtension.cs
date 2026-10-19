using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeExec;

public sealed class ExecExtension : IPiSharpExtension
{
    public static PiSharpExtensionProcessExecutor? CapturedExecutor { get; private set; }

    public void Configure(IPiSharpExtensionBuilder builder)
    {
        var executor = builder.ProcessExecutor;
        CapturedExecutor = executor;
        var assembly = typeof(ExecExtension).Assembly.Location;
        builder.AddCommand("native-exec", (text, token) => RunAsync(text, token));
        builder.AddTool(AIFunctionFactory.Create(RunAsync, "native_exec"));
        builder.AddSessionStartHandler(async (_, _, token) =>
        {
            var result = await executor.ExecuteAsync("dotnet", [assembly, "output", "event-output", "event-error", "0"],
                cancellationToken: token);
            var log = Environment.GetEnvironmentVariable("PISHARP_NATIVE_EXEC_EVENT_LOG");
            if (log is not null) await File.WriteAllTextAsync(log, JsonSerializer.Serialize(result), token);
        });
        if (Environment.GetEnvironmentVariable("PISHARP_NATIVE_EXEC_FAIL_CONFIGURE") is { } marker)
        {
            _ = executor.ExecuteAsync("dotnet", [assembly, "wait", marker]);
            Program.WaitForFileAsync(marker).GetAwaiter().GetResult();
            throw new InvalidOperationException("exec fixture Configure failure");
        }

        async Task<string> RunAsync(string text, CancellationToken token)
        {
            var result = text.StartsWith("wait:", StringComparison.Ordinal)
                ? await executor.ExecuteAsync("dotnet", [assembly, "tree", text[5..]], cancellationToken: token)
                : await executor.ExecuteAsync("dotnet", [assembly, "output", text, "fixture-error", "7"],
                    cancellationToken: token);
            return JsonSerializer.Serialize(result);
        }
    }
}
