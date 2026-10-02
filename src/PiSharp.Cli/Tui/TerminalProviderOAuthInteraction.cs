namespace PiSharp.Cli.Tui;

internal sealed class TerminalProviderOAuthInteraction(TerminalEditor editor, string providerId) : IProviderOAuthInteraction
{
    private readonly ConsoleProviderOAuthInteraction _console = new();
    private readonly object _inputGate = new();

    public bool TryReadAbort()
    {
        if (!Monitor.TryEnter(_inputGate)) return false;
        try { return editor.TryReadLoginAbort(); }
        finally { Monitor.Exit(_inputGate); }
    }

    public Task<string> SelectLoginMethodAsync(IReadOnlyList<ProviderOAuthLoginMethod> methods,
        CancellationToken cancellationToken)
    {
        lock (_inputGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (providerId == "radius")
                Console.WriteLine("Radius is a service crafted for Pi by the builders of Pi, Earendil Works");
            var choice = editor.ShowSelectionList("Select an OAuth login method:", methods.Select(method =>
                new TerminalSelectionOption<string>(method.Id, method.Id, method.Label)).ToArray());
            return Task.FromResult(choice?.Option.Value ?? throw new OperationCanceledException("Login cancelled"));
        }
    }

    public Task<string> PromptForCodeAsync(string message, string placeholder, CancellationToken cancellationToken)
    {
        lock (_inputGate) return _console.PromptForCodeAsync(message, placeholder, cancellationToken);
    }

    public void Notify(ProviderOAuthNotice notice) => _console.Notify(notice);
}
