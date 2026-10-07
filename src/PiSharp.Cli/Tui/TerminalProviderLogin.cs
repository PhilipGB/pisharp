using PiSharp.Cli.Authentication;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalProviderLogin(ProviderModelRuntime runtime, TerminalEditor editor,
    string agentDirectory, Func<string, string?> environment, Func<string> workingDirectory, Func<string> currentProvider,
    Func<Task> synchronizeCurrentProvider, Func<Task> reload)
{
    public async Task ShowAsync(string arguments, CancellationToken cancellationToken = default)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 2) throw new ArgumentException("Use /login [provider] [api-key|oauth].");
        if (parts.Length > 0)
        {
            var provider = runtime.GetProvider(parts[0]);
            var type = parts.ElementAtOrDefault(1);
            while (true)
            {
                type ??= provider.OAuthSupported && !provider.ApiKeySupported || provider.Id == "radius" ? "oauth"
                    : !provider.OAuthSupported ? "api-key" : SelectAuthenticationType(provider.Name);
                if (type is null) return;
                if (await SignInAsync(provider, type, cancellationToken)) return;
                if (parts.Length == 2 || provider.Id == "radius" || !provider.OAuthSupported || !provider.ApiKeySupported) return;
                type = null;
            }
        }

        while (true)
        {
            var choices = new List<TerminalSelectionOption<string>>
            {
                new("oauth", "oauth", "Sign in with an account"),
                new("api-key", "api-key", "Sign in with an API key")
            };
            var radius = runtime.Providers.FirstOrDefault(provider => provider.Id == "radius" && provider.OAuthSupported);
            if (radius is not null)
            {
                var animationStart = System.Diagnostics.Stopwatch.GetTimestamp();
                var status = await runtime.ResolveAuthAsync("radius", cancellationToken: cancellationToken,
                    allowOAuthRefresh: false);
                choices.Add(new("radius", "radius", "Sign in with Radius" +
                    (status.Authenticated ? status.Source.StartsWith("stored OAuth", StringComparison.Ordinal)
                        ? " ✓ configured" : " • API key configured" : " • not configured"),
                    SelectedLabelRenderer: label => RadiusLoginShimmer.Paint("Sign in with Radius",
                        System.Diagnostics.Stopwatch.GetElapsedTime(animationStart).TotalMilliseconds, editor.CurrentTheme.Mode) +
                        label["Sign in with Radius".Length..]));
            }
            var selected = editor.ShowSelectionList("Select authentication method:", choices);
            if (selected is null) return;
            if (selected.Option.Value == "radius")
            {
                if (await SignInAsync(radius!, "oauth", cancellationToken)) return;
                continue;
            }
            var type = selected.Option.Value;
            while (true)
            {
                var providers = runtime.Providers.Where(provider => type == "oauth" ? provider.OAuthSupported : provider.ApiKeySupported)
                    .OrderBy(provider => provider.Name, StringComparer.Ordinal).Select(provider =>
                        new TerminalSelectionOption<ProviderProfile>(provider.Id, provider, provider.Name,
                            type == "oauth" ? provider.Id == "radius" ? "account" : "subscription" : "API key")).ToArray();
                var provider = editor.ShowSelectionList(type == "oauth" ? "Select account provider:" : "Select API key provider:",
                    providers, selectedKey: currentProvider());
                if (provider is null) break;
                if (await SignInAsync(provider.Option.Value, type, cancellationToken)) return;
            }
        }
    }

    private string? SelectAuthenticationType(string name) => editor.ShowSelectionList(
        $"Select authentication method for {name}:", new[]
        {
            new TerminalSelectionOption<string>("oauth", "oauth", "Sign in with an account"),
            new TerminalSelectionOption<string>("api-key", "api-key", "Sign in with an API key")
        })?.Option.Value;

    private async Task<bool> SignInAsync(ProviderProfile provider, string type, CancellationToken cancellationToken)
    {
        if (type is not ("api-key" or "oauth")) throw new ArgumentException("Use /login [provider] [api-key|oauth].");
        if (type == "oauth" && !provider.OAuthSupported)
            throw new InvalidOperationException($"Provider '{provider.Id}' has no configured OAuth adapter.");
        if (type == "api-key" && !provider.ApiKeySupported)
            throw new InvalidOperationException($"Provider '{provider.Id}' requires its OAuth login flow.");
        try
        {
            if (provider.Id == "llama.cpp")
            {
                if (type != "api-key") throw new ArgumentException("Use /login llama.cpp.");
                var environmentUrl = environment("LLAMA_BASE_URL");
                var defaultUrl = string.IsNullOrWhiteSpace(environmentUrl)
                    ? LlamaRouterClient.DefaultServerUrl : environmentUrl.Trim();
                Console.Error.WriteLine($"llama.cpp server URL (Enter to use {defaultUrl}):");
                var enteredUrl = await editor.ReadLineAsync(_ => Task.CompletedTask, enableApplicationActions: false,
                    allowEmptySubmit: true);
                if (enteredUrl is null) return false;
                var serverUrl = string.IsNullOrWhiteSpace(enteredUrl) ? defaultUrl : enteredUrl.Trim();

                Console.Error.Write("API key for llama.cpp (optional): ");
                var secret = ReadSecret();
                if (secret is null) return false;
                await runtime.LoginLlamaRouterAsync(string.IsNullOrWhiteSpace(secret) ? null : secret,
                    serverUrl, cancellationToken);
            }
            else if (type == "oauth")
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var interaction = new TerminalProviderOAuthInteraction(editor, provider.Id);
                var login = runtime.LoginOAuthAsync(provider.Id, interaction, cancel.Token);
                while (!login.IsCompleted)
                {
                    if (interaction.TryReadAbort()) cancel.Cancel();
                    await Task.WhenAny(login, Task.Delay(50, cancellationToken));
                }
                await login;
            }
            else
            {
                Console.Error.Write($"API key for {provider.Id}: ");
                var secret = ReadSecret();
                if (secret is null) return false;
                if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("Credential cannot be empty.");
                await runtime.LoginApiKeyAsync(provider.Id, secret);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        Console.WriteLine(provider.Id == "llama.cpp"
            ? "Authenticated llama.cpp; credential value was not displayed."
            : $"Authenticated {provider.Id} with {type}; credential value was not displayed.");
        if (provider.Id.Equals(currentProvider(), StringComparison.OrdinalIgnoreCase))
            await synchronizeCurrentProvider();
        if (provider.Id == "radius" && type == "oauth")
            await OfferRadiusMcpAsync(cancellationToken);
        return true;
    }

    private async Task OfferRadiusMcpAsync(CancellationToken cancellationToken)
    {
        var setup = await RadiusMcpSetup.CreateAsync(agentDirectory, workingDirectory(), cancellationToken);
        if (setup is null) return;
        var choice = editor.ShowSelectionList($"Configure Radius MCP in {setup.Path}?", new[]
        {
            new TerminalSelectionOption<bool>("yes", true, "Yes"),
            new TerminalSelectionOption<bool>("no", false, "No")
        });
        if (choice?.Option.Value != true) return;
        try { await setup.SaveAsync(cancellationToken); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            Console.Error.WriteLine($"Could not update {setup.Path}: {error.Message}");
            return;
        }
        await reload();
    }

    private static string? ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine();
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Escape) { Console.Error.WriteLine(); return null; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
    }
}
