using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;

namespace PiSharp.Cli;

/// <summary>
/// Interactive /login and /logout (pinned handleLoginCommand / showOAuthSelector, text
/// variant). Login runs the provider's api-key or OAuth flow through
/// ModelRuntime.LoginAsync with a console-backed interaction; logout removes only the
/// stored credential (pinned: environment variables and models.json config are unchanged)
/// and performs no provider failover — a lost key surfaces as the usual "No API key for
/// provider/model" error on the next prompt.
/// </summary>
internal static class LoginCommands
{
    private const int CredentialOperationTimeoutMs = 15_000;

    /// <summary>One selectable login option: provider × auth method (pinned AuthSelectorProvider).</summary>
    public sealed record LoginOption(string Id, string Name, string AuthType, string? StatusLabel);

    /// <summary>
    /// Login options for the runtime (pinned getLoginProviderOptions): every registered
    /// provider contributes one option per declared auth method, sorted by name.
    /// </summary>
    public static IReadOnlyList<LoginOption> GetLoginProviderOptions(ModelRuntime runtime, string? authType = null)
    {
        var options = new List<LoginOption>();
        foreach (var provider in runtime.GetProviders())
        {
            var status = runtime.GetProviderAuthStatus(provider.Id);
            var statusLabel = status.Configured
                ? $"{(runtime.IsUsingOAuth(provider.Id) ? "oauth" : "api_key")} ({status.Label ?? status.Source})"
                : null;
            if ((authType is null || authType == "oauth") && provider.Auth.OAuth is not null)
            {
                options.Add(new LoginOption(provider.Id, provider.Name, "oauth", statusLabel));
            }

            if ((authType is null || authType == "api_key") && provider.Auth.ApiKey is not null)
            {
                options.Add(new LoginOption(provider.Id, provider.Name, "api_key", statusLabel));
            }
        }

        return options
            .OrderBy(option => option.Name, StringComparer.Ordinal)
            .ThenBy(option => option.AuthType, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Stored-credential options for /logout (pinned getLogoutProviderOptions).
    /// </summary>
    public static async Task<IReadOnlyList<LoginOption>> GetLogoutProviderOptionsAsync(
        ModelRuntime runtime, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CredentialOperationTimeoutMs);
        var credentials = await runtime.ListCredentialsAsync(cts.Token);
        return credentials
            .Select(info => new LoginOption(
                info.ProviderId,
                runtime.GetProvider(info.ProviderId)?.Name ?? info.ProviderId,
                info.Type,
                "stored credential"))
            .OrderBy(option => option.Name, StringComparer.Ordinal)
            .ThenBy(option => option.AuthType, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// /login [provider]. Without an argument the full option list is shown; with one the
    /// options are filtered by provider id or name (pinned findLoginProviderOptions) and a
    /// single match starts immediately.
    /// </summary>
    public static async Task HandleLoginAsync(
        string? argument,
        SessionController sessions,
        ConsoleAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        var runtime = sessions.ModelRuntime;
        var allOptions = GetLoginProviderOptions(runtime);
        var options = argument is null
            ? allOptions
            : allOptions.Where(option =>
                string.Equals(option.Id, argument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.Name, argument, StringComparison.OrdinalIgnoreCase)).ToList();

        if (options.Count == 0)
        {
            Console.WriteLine("No login providers available.");
            return;
        }

        var selected = options.Count == 1
            ? options[0]
            : await PickOptionAsync(options, "Select a login option (blank to cancel): ");
        if (selected is null)
        {
            return;
        }

        await StartProviderLoginAsync(selected, sessions, interaction, cancellationToken);
    }

    /// <summary>
    /// /logout [provider]. Removes the stored credential for the provider (pinned
    /// showOAuthSelector logout branch) and reports with the pinned messages.
    /// </summary>
    public static async Task HandleLogoutAsync(
        string? argument,
        SessionController sessions,
        CancellationToken cancellationToken)
    {
        var runtime = sessions.ModelRuntime;
        IReadOnlyList<LoginOption> options;
        try
        {
            options = await GetLogoutProviderOptionsAsync(runtime, cancellationToken);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Could not read stored credentials: {error.Message}");
            return;
        }

        if (argument is not null)
        {
            var match = options.FirstOrDefault(option =>
                string.Equals(option.Id, argument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.Name, argument, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                Console.WriteLine(
                    "No stored credentials to remove. /logout only removes credentials saved by /login; environment variables and models.json config are unchanged.");
                return;
            }

            options = [match];
        }

        if (options.Count == 0)
        {
            Console.WriteLine(
                "No stored credentials to remove. /logout only removes credentials saved by /login; environment variables and models.json config are unchanged.");
            return;
        }

        var selected = options.Count == 1
            ? options[0]
            : await PickOptionAsync(options, "Select a stored credential to remove (blank to cancel): ");
        if (selected is null)
        {
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(CredentialOperationTimeoutMs);
            await runtime.LogoutAsync(selected.Id, cts.Token);
            Console.WriteLine(selected.AuthType == "oauth"
                ? $"Logged out of {selected.Name}"
                : $"Removed stored API key for {selected.Name}. Environment variables and models.json config are unchanged.");
        }
        catch (CredentialSynchronizationError error)
        {
            Console.Error.WriteLine(
                $"Credentials removed for {selected.Name}, but local model state could not be synchronized: {error.Message}");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Logout failed: {error.Message}");
        }
    }

    private static async Task StartProviderLoginAsync(
        LoginOption option,
        SessionController sessions,
        ConsoleAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        var previousModel = sessions.ModelState.Model;
        try
        {
            await sessions.ModelRuntime.LoginAsync(option.Id, option.AuthType, interaction);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("Login cancelled.");
            return;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Login failed: {error.Message}");
            return;
        }

        await CompleteProviderAuthenticationAsync(option, previousModel, sessions, cancellationToken);
    }

    /// <summary>Post-login selection outcome (pinned completeProviderAuthentication).</summary>
    internal sealed record PostLoginSelection(
        string ActionLabel,
        string AuthPath,
        ModelInfo? SelectedModel,
        string? SelectionError);

    /// <summary>
    /// Post-login model selection (pinned completeProviderAuthentication): when the session
    /// has no model, the provider's default model is selected and persisted; otherwise the
    /// existing model stays and only the credential save is reported. Kept pure so the
    /// pinned precedence is directly testable.
    /// </summary>
    internal static PostLoginSelection ResolvePostLoginSelection(
        LoginOption option,
        ModelInfo? previousModel,
        ModelRuntime runtime)
    {
        var actionLabel = option.AuthType == "oauth"
            ? $"Logged in to {option.Name}"
            : $"Saved API key for {option.Name}";
        var authPath = Path.Combine(ModelStartup.GetAgentDirectory(), "auth.json");
        if (previousModel is not null)
        {
            return new PostLoginSelection(actionLabel, authPath, null, null);
        }

        var providerModels = runtime.GetAvailableSnapshot()
            .Where(model => model.Provider == option.Id)
            .ToList();
        if (option.Id == BuiltinProviders.LlamaCppProviderId)
        {
            // Pinned defers to the /llama extension, which PiSharp does not port; the local
            // workflow is --model + --endpoint instead.
            return new PostLoginSelection(
                actionLabel,
                authPath,
                null,
                providerModels.Count == 0
                    ? $"{actionLabel}. No llama.cpp models are loaded. Start a server and use --model + --endpoint, then /model to select it."
                    : $"{actionLabel}. Use /model to select a loaded llama.cpp model.");
        }

        if (!ModelResolver.DefaultModelPerProvider.TryGetValue(option.Id, out var defaultModelId))
        {
            return new PostLoginSelection(
                actionLabel, authPath, null,
                $"{actionLabel}, but no default model is configured for provider \"{option.Id}\". Use /model to select a model.");
        }

        if (providerModels.Count == 0)
        {
            return new PostLoginSelection(
                actionLabel, authPath, null,
                $"{actionLabel}, but no models are available for that provider. Use /model to select a model.");
        }

        var selected = providerModels.FirstOrDefault(model => model.Id == defaultModelId);
        if (selected is null)
        {
            return new PostLoginSelection(
                actionLabel, authPath, null,
                $"{actionLabel}, but its default model \"{defaultModelId}\" is not available. Use /model to select a model.");
        }

        return new PostLoginSelection(actionLabel, authPath, selected, null);
    }

    private static async Task CompleteProviderAuthenticationAsync(
        LoginOption option,
        ModelInfo? previousModel,
        SessionController sessions,
        CancellationToken cancellationToken)
    {
        var selection = ResolvePostLoginSelection(option, previousModel, sessions.ModelRuntime);
        if (selection.SelectedModel is not null)
        {
            try
            {
                await sessions.SetModelAsync(
                    selection.SelectedModel, new ModelMutationOptions(Persist: true), cancellationToken);
                Console.WriteLine($"{selection.ActionLabel}. Selected {selection.SelectedModel.Id}. Credentials saved to {selection.AuthPath}");
                return;
            }
            catch (Exception error)
            {
                Console.WriteLine($"{selection.ActionLabel}. Credentials saved to {selection.AuthPath}");
                Console.Error.WriteLine(
                    $"{selection.ActionLabel}, but selecting its default model failed: {error.Message}. Use /model to select a model.");
                return;
            }
        }

        Console.WriteLine($"{selection.ActionLabel}. Credentials saved to {selection.AuthPath}");
        if (selection.SelectionError is not null)
        {
            Console.Error.WriteLine(selection.SelectionError);
        }
    }

    /// <summary>Shows a numbered list and reads a 1-based selection (blank cancels).</summary>
    private static async Task<LoginOption?> PickOptionAsync(IReadOnlyList<LoginOption> options, string prompt)
    {
        Console.WriteLine("Login options:");
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var status = option.StatusLabel is not null ? $"  [{option.StatusLabel}]" : string.Empty;
            Console.WriteLine($"  {i + 1,2}. {option.Name} ({option.Id}) — {option.AuthType}{status}");
        }

        Console.Write(prompt);
        var input = await Task.Run(() => Console.ReadLine(), CancellationToken.None);
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        return int.TryParse(input, out var selected) && selected >= 1 && selected <= options.Count
            ? options[selected - 1]
            : throw new ArgumentException("Invalid selection.");
    }

    /// <summary>
    /// Console-backed login interaction (pinned AuthInteraction): prompts print to stdout,
    /// secrets are read from the console line (echoing is a documented text-CLI difference
    /// from the pinned TUI's masked input).
    /// </summary>
    internal sealed class ConsoleAuthInteraction : IAuthInteraction
    {
        private readonly CancellationToken _signal;

        public ConsoleAuthInteraction(CancellationToken signal) => _signal = signal;

        /// <inheritdoc />
        public CancellationToken Signal => _signal;

        /// <inheritdoc />
        public async Task<string> PromptAsync(AuthPromptStep prompt, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _signal, cancellationToken, prompt.PromptCancellationToken);
            switch (prompt)
            {
                case TextPromptStep text:
                    return await PromptCoreAsync(text.Message, linked.Token);
                case SecretPromptStep secret:
                    return await PromptCoreAsync(
                        $"{secret.Message} (input is echoed in this text interface)", linked.Token);
                case ManualCodePromptStep code:
                    return await PromptCoreAsync(code.Message, linked.Token);
                case SelectPromptStep select:
                    Console.WriteLine(select.Message);
                    for (var i = 0; i < select.Options.Count; i++)
                    {
                        var option = select.Options[i];
                        var description = option.Description is not null ? $" — {option.Description}" : string.Empty;
                        Console.WriteLine($"  {i + 1,2}. {option.Label}{description}");
                    }

                    var input = await PromptCoreAsync($"Select an option [1-{select.Options.Count}]: ", linked.Token);
                    return int.TryParse(input, out var selected) && selected >= 1 && selected <= select.Options.Count
                        ? select.Options[selected - 1].Id
                        : throw new ArgumentException("Invalid selection.");
                default:
                    return await PromptCoreAsync(prompt.ToString() ?? string.Empty, linked.Token);
            }
        }

        private static async Task<string> PromptCoreAsync(string message, CancellationToken cancellationToken)
        {
            Console.Write(message);
            var input = await Task.Run(() => Console.ReadLine(), cancellationToken);
            if (input is null)
            {
                throw new OperationCanceledException("Prompt closed.");
            }

            return input.Trim();
        }

        /// <inheritdoc />
        public void Notify(AuthEvent evt)
        {
            switch (evt)
            {
                case AuthEvent.InfoEvent info:
                    Console.WriteLine(info.Message);
                    if (info.Links is { Count: > 0 })
                    {
                        foreach (var link in info.Links)
                        {
                            Console.WriteLine($"  {link.Label ?? link.Url}");
                        }
                    }
                    break;
                case AuthEvent.AuthUrlEvent url:
                    Console.WriteLine($"Open this URL in a browser: {url.Url}");
                    if (url.Instructions is not null)
                    {
                        Console.WriteLine(url.Instructions);
                    }
                    break;
                case AuthEvent.DeviceCodeEvent device:
                    Console.WriteLine($"Enter code {device.UserCode} at {device.VerificationUri}");
                    break;
                case AuthEvent.ProgressEvent progress:
                    Console.WriteLine(progress.Message);
                    break;
            }
        }
    }
}
