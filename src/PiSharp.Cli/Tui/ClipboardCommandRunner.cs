using System.ComponentModel;
using System.Diagnostics;

namespace PiSharp.Cli.Tui;

internal sealed record ClipboardCommandResult(int ExitCode, byte[] Output);

internal interface IClipboardCommandRunner
{
    Task<ClipboardCommandResult?> RunAsync(string command, IReadOnlyList<string> arguments, string? input,
        int maximumOutputBytes, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Runs OS clipboard helpers without a shell, with bounded output and a fixed timeout.</summary>
internal sealed class ClipboardCommandRunner : IClipboardCommandRunner
{
    private const int MaximumErrorBytes = 64 * 1024;

    public async Task<ClipboardCommandResult?> RunAsync(string command, IReadOnlyList<string> arguments, string? input,
        int maximumOutputBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = input is null,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = new Process { StartInfo = start };
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }

        using (process)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(timeout);
            try
            {
                var output = input is null
                    ? ReadBoundedAsync(process.StandardOutput.BaseStream, maximumOutputBytes, deadline.Token)
                    : Task.FromResult(Array.Empty<byte>());
                var stderr = ReadBoundedAsync(process.StandardError.BaseStream, MaximumErrorBytes, deadline.Token);
                var write = input is null ? Task.CompletedTask : WriteInputAsync(process, input, deadline.Token);
                await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, stderr, write);
                return new(process.ExitCode, await output);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                return null;
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
            {
                Kill(process);
                return null;
            }
        }
    }

    private static async Task WriteInputAsync(Process process, string input, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (IOException) { }
        finally { process.StandardInput.Close(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumOutputBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var length = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (length == 0) return output.ToArray();
            if (output.Length + length > maximumOutputBytes)
                throw new InvalidDataException("Clipboard command output exceeded its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
