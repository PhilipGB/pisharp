namespace PiSharp.Cli;

/// <summary>
/// Console primitive seam for all interactive prompts (item 4).
///
/// On an interactive terminal every prompt reads input key-by-key
/// (<see cref="KeyAvailable"/> polling plus <see cref="ReadKey"/>) instead of a blocking
/// line read. That is a hard constraint, not a style choice: on Unix a blocked
/// <c>read(2)</c> on the TTY is retried on EINTR (dotnet/runtime System.Console
/// StdInReader/KeyParser), so a <c>Console.ReadLine</c>/<c>ReadKey</c> that is blocked when
/// SIGINT (Ctrl+C) arrives cannot be interrupted, and the previous
/// <c>Task.Run(() =&gt; Console.ReadLine(), token)</c> pattern left an orphan thread that
/// the token could never cancel while the prompt appeared unresponsive. A key-driven loop
/// only blocks while a key is actually in flight, observes the cancellation token between
/// keys, and treats a lone Esc as cancel — so prompts are maskable, cancellable, and
/// never leave an orphaned read.
///
/// On redirected stdin there is no TTY to poll: <see cref="ReadLineSync"/> is the fallback.
/// Masking and Esc are impossible on a pipe (documented deviation); the read blocks until
/// data or EOF.
/// </summary>
public interface IConsoleIO
{
    /// <summary>True when stdin is an interactive terminal (keys can be read individually).</summary>
    bool IsInteractive { get; }

    /// <summary>True when a key is already queued (non-blocking).</summary>
    bool KeyAvailable { get; }

    /// <summary>Writes to stdout without a newline.</summary>
    void Write(string text);

    /// <summary>Writes to stdout with a newline.</summary>
    void WriteLine(string text);

    /// <summary>Reads one key, intercepting it (not echoed by the terminal). Call only when <see cref="KeyAvailable"/>.</summary>
    ConsoleKeyInfo ReadKey();

    /// <summary>Reads one line from redirected input; null on EOF.</summary>
    string? ReadLineSync();
}

/// <summary>Production <see cref="IConsoleIO"/> backed by <see cref="System.Console"/>.</summary>
public sealed class SystemConsoleIO : IConsoleIO
{
    public bool IsInteractive => !Console.IsInputRedirected;
    public bool KeyAvailable => Console.KeyAvailable;
    public void Write(string text) => Console.Write(text);
    public void WriteLine(string text) => Console.WriteLine(text);
    public ConsoleKeyInfo ReadKey() => Console.ReadKey(intercept: true);
    public string? ReadLineSync() => Console.In.ReadLine();
}
