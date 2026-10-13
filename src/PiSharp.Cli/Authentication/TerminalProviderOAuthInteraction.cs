namespace PiSharp.Cli.Tui;

internal sealed class TerminalProviderOAuthInteraction(TerminalEditor editor, string providerId, string providerName,
    Func<Task> copyAuthorizationUrl) : IProviderOAuthInteraction
{
    private readonly ConsoleProviderOAuthInteraction _console = new();
    private readonly object _inputGate = new();
    private bool _manualCodePromptActive;

    public async Task<bool> TryReadAbortAsync()
    {
        if (!Monitor.TryEnter(_inputGate)) return false;
        TerminalLoginInputAction? action;
        try { action = _manualCodePromptActive ? null : editor.TryReadLoginAction(); }
        finally { Monitor.Exit(_inputGate); }
        if (action == TerminalLoginInputAction.CopyAuthorizationUrl)
        {
            if (editor.ActiveAuthorizationUrl is not null) await copyAuthorizationUrl();
            return false;
        }
        return action == TerminalLoginInputAction.Cancel;
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
        lock (_inputGate) _manualCodePromptActive = true;
        return PromptForCodeCoreAsync(message, placeholder, cancellationToken);
    }

    private async Task<string> PromptForCodeCoreAsync(string message, string placeholder,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!editor.IsScreenActive)
                return await _console.PromptForCodeAsync(message, placeholder, cancellationToken);
            var prompts = await editor.PromptSequenceAsync("Login to " + providerName,
                [(message, placeholder)], cancellationToken: cancellationToken,
                dispatchApplicationAction: _ => copyAuthorizationUrl());
            return prompts is { Count: > 0 } ? prompts[0] :
                throw new OperationCanceledException("Login cancelled", cancellationToken);
        }
        finally
        {
            lock (_inputGate) _manualCodePromptActive = false;
        }
    }

    public void Notify(ProviderOAuthNotice notice)
    {
        if (notice.Kind == "auth_url" && notice.Url is { } url &&
            editor.PresentProviderAuthorizationUrl(providerId, providerName, notice.Message, url))
            return;
        _console.Notify(notice);
    }
}
