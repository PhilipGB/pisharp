using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PiSharp.TestExtensions.NativeExec;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        switch (args[0])
        {
            case "inspect":
                Console.Write(JsonSerializer.Serialize(new
                {
                    Arguments = args[1..],
                    WorkingDirectory = Environment.CurrentDirectory,
                    Environment = Environment.GetEnvironmentVariable("PATH"),
                    Input = Console.Read()
                }));
                return 0;
            case "output":
                Console.Write(args[1].Replace('␀', '\0'));
                Console.Error.Write(args[2]);
                return int.Parse(args[3]);
            case "release":
                WriteMarker(args[1]);
                await WaitForFileAsync(args[1] + ".release");
                Console.Write("released");
                return 0;
            case "large":
                var count = int.Parse(args[1]);
                var block = Encoding.UTF8.GetBytes(new string('a', 8191) + "€漢🙂\u001b[31m\r\n");
                await Task.WhenAll(WriteAsync(Console.OpenStandardOutput()), WriteAsync(Console.OpenStandardError()));
                return 0;

                async Task WriteAsync(Stream stream)
                {
                    for (var index = 0; index < count; index++)
                    {
                        await stream.WriteAsync(block.AsMemory(0, 8192));
                        await stream.FlushAsync();
                        await stream.WriteAsync(block.AsMemory(8192));
                    }
                }
            case "wait":
                using (var ignored = !OperatingSystem.IsWindows() && args.Contains("ignore-term")
                    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => context.Cancel = true) : null)
                {
                    WriteMarker(args[1]);
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                }
                return 0;
            case "tree":
                using (var child = Start("wait", args[1] + ".child", "ignore-term"))
                {
                    await WaitForFileAsync(args[1] + ".child");
                    Console.Write("ready-output");
                    Console.Error.Write("ready-error");
                    await Console.Out.FlushAsync();
                    await Console.Error.FlushAsync();
                    WriteMarker(args[1]);
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                }
                return 0;
            case "inherit":
                using (var child = Start(args[2], args[1]))
                    await WaitForFileAsync(args[1]);
                WriteMarker(args[1] + ".parent");
                return args.Length > 3 ? int.Parse(args[3]) : 0;
            case "stream":
                WriteMarker(args[1]);
                using (var output = Console.OpenStandardOutput())
                {
                    while (true)
                    {
                        output.Write("active-output"u8);
                        Thread.Sleep(5);
                    }
                }
            case "writer":
                WriteMarker(args[1]);
                using (var output = Console.OpenStandardOutput())
                using (var error = Console.OpenStandardError())
                {
                    for (var index = 0; index < 100_000; index++)
                    {
                        output.Write("tail"u8);
                        error.Write("err"u8);
                    }
                }
                return 0;
            default:
                throw new ArgumentException("Unknown fixture mode.");
        }
    }

    private static Process Start(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static void WriteMarker(string path)
    {
        File.WriteAllText(path + ".tmp", Environment.ProcessId.ToString());
        File.Move(path + ".tmp", path);
    }

    internal static async Task WaitForFileAsync(string path)
    {
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => ready.TrySetResult();
        watcher.Renamed += (_, _) => ready.TrySetResult();
        watcher.EnableRaisingEvents = true;
        if (File.Exists(path)) ready.TrySetResult();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
