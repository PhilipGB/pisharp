using System.Text.Json;
using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;

namespace PiSharp.Cli;

/// <summary>Error raised by auth command parsing/validation (pinned AuthCommandError).</summary>
internal sealed class AuthCommandError(string message) : Exception(message);

/// <summary>One parsed auth subcommand (pinned AuthCommand).</summary>
internal sealed record AuthCommand(
    AuthCommandKind Kind,
    string[] Args,
    bool Json,
    bool Credentials,
    bool NoRefresh,
    int? MinExpiryMs);

/// <summary>Auth subcommand kinds (pinned AuthCommandKind).</summary>
internal enum AuthCommandKind
{
    Check,
    PrintApiKey,
    PrintBearerToken,
}

/// <summary>
/// The <c>pisharp auth</c> subcommand: check / print-api-key / print-bearer-token, ported
/// from pinned auth-command.ts, auth-check.ts, and credential-print.ts. Output, error
/// messages, and exit codes (0 ready, 1 not ready, 2 invalid/parse error for check) follow
/// the pinned CLI exactly.
/// </summary>
internal static class AuthCommands
{
    private const string AppName = "pisharp";
    private const int DefaultBearerTokenMinExpiryMs = 30 * 60_000;
    private const int CredentialPrintTimeoutMs = 15_000;

    /// <summary>True for the pinned isAuthCommandHelp: "auth" with no subcommand, "auth help", or a help flag.</summary>
    public static bool IsHelp(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        args[0] == "auth" &&
        (args.Count == 1 || args[1] == "help" || args.Contains("--help") || args.Contains("-h"));

    /// <summary>Prints the pinned auth help text to stdout.</summary>
    public static void PrintHelp()
    {
        Console.WriteLine("""
            Usage:
              pisharp auth print-api-key [--provider <provider>] [--model <model>]
              pisharp auth print-bearer-token [--provider <provider>] [--model <model>] [--min-expiry <duration>]
              pisharp auth check [--provider <provider>] [--model <model>] [--json] [--credentials] [--no-refresh]

            Auth commands require at least one of --provider or --model. Checks refresh expired OAuth credentials by default; --no-refresh prevents this. --credentials emits the credential, or includes it in JSON output.
            """);
    }

    /// <summary>Pinned getAuthCommandName.</summary>
    public static string GetCommandName(AuthCommandKind kind) => kind switch
    {
        AuthCommandKind.Check => "auth check",
        AuthCommandKind.PrintApiKey => "auth print-api-key",
        AuthCommandKind.PrintBearerToken => "auth print-bearer-token",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Pinned getAuthCommandUsage.</summary>
    public static string GetCommandUsage(AuthCommandKind kind) => kind switch
    {
        AuthCommandKind.Check => $"{AppName} auth check --provider <provider> [--json] [--credentials] [--no-refresh]",
        AuthCommandKind.PrintApiKey => $"{AppName} auth print-api-key --provider <provider> [--model <model>]",
        AuthCommandKind.PrintBearerToken => $"{AppName} auth print-bearer-token --provider <provider> [--model <model>] [--min-expiry <duration>]",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Parses "auth &lt;kind&gt; ..." arguments (pinned parseAuthCommand). The remaining
    /// positional flag args (e.g. --provider/--model) are returned on the command for the
    /// normal option parser.
    /// </summary>
    public static AuthCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "auth")
        {
            throw new AuthCommandError("Use \"pisharp auth check\", \"pisharp auth print-api-key\", or \"pisharp auth print-bearer-token\".");
        }

        var kind = args.Count > 1 && args[1] == "check"
            ? AuthCommandKind.Check
            : args.Count > 1 && args[1] == "print-api-key"
                ? AuthCommandKind.PrintApiKey
                : args.Count > 1 && args[1] == "print-bearer-token"
                    ? AuthCommandKind.PrintBearerToken
                    : throw CreateUnknownAuthCommandError(args);

        var commandArgs = new List<string>();
        var json = false;
        var credentials = false;
        var noRefresh = false;
        int? minExpiryMs = null;
        for (var i = 2; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--min-expiry")
            {
                if (kind != AuthCommandKind.PrintBearerToken)
                {
                    throw new AuthCommandError("--min-expiry is only supported by print-bearer-token");
                }

                var value = i + 1 < args.Count ? args[i + 1] : null;
                var match = System.Text.RegularExpressions.Regex.Match(value ?? string.Empty, @"^(\d+)(ms|s|m|h)$");
                if (!match.Success)
                {
                    throw new AuthCommandError("--min-expiry must use a duration such as 30m or 1h");
                }

                var amount = int.Parse(match.Groups[1].Value);
                var unit = match.Groups[2].Value;
                minExpiryMs = amount * (unit == "ms" ? 1 : unit == "s" ? 1_000 : unit == "m" ? 60_000 : 3_600_000);
                i++;
                continue;
            }

            if (arg is "--json" or "--credentials" or "--no-refresh")
            {
                if (kind != AuthCommandKind.Check)
                {
                    throw new AuthCommandError($"{arg} is only supported by auth check");
                }

                if (arg == "--json")
                {
                    json = true;
                }
                else if (arg == "--credentials")
                {
                    credentials = true;
                }
                else
                {
                    noRefresh = true;
                }

                continue;
            }

            commandArgs.Add(arg);
        }

        return new AuthCommand(kind, commandArgs.ToArray(), json, credentials, noRefresh, minExpiryMs);
    }

    private static AuthCommandError CreateUnknownAuthCommandError(IReadOnlyList<string> args)
    {
        var unknown = args.Count > 1 ? args[1] : string.Empty;
        return new AuthCommandError(
            $"Unknown auth command \"{unknown}\". Use \"{AppName} auth print-api-key\", \"{AppName} auth print-bearer-token\", or \"{AppName} auth check\".");
    }

    /// <summary>
    /// Resolves the provider/model from the parsed option args (pinned validateAuthCommandArgs).
    /// Returns (provider, model); throws AuthCommandError with the pinned messages.
    /// </summary>
    public static (string? Provider, string? Model) ValidateArgs(CliOptions options, AuthCommandKind kind)
    {
        var provider = string.IsNullOrWhiteSpace(options.Provider) ? null : options.Provider.Trim();
        var model = string.IsNullOrWhiteSpace(options.Model) ? null : options.Model.Trim();
        if (!string.IsNullOrWhiteSpace(options.ApiKey) || options.Prompt is not null || options.FilePaths.Count > 0)
        {
            throw new AuthCommandError("Auth commands only accept --provider and --model");
        }

        if (provider is null && model is null)
        {
            throw new AuthCommandError(kind == AuthCommandKind.Check
                ? "Auth checks require --provider <provider> or --model <model>"
                : "Credential printing requires --provider <provider> or --model <model>");
        }

        return (provider, model);
    }

    /// <summary>
    /// Runs one auth command to completion (pinned runAuthCommand body). Returns the process
    /// exit code; output goes to stdout and errors to stderr exactly as pinned.
    /// </summary>
    public static async Task<int> RunAsync(AuthCommand command, CliOptions options, CancellationToken cancellationToken)
    {
        try
        {
            if (command.Kind != AuthCommandKind.Check)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(CredentialPrintTimeoutMs);
                var runtime = await CreatePrintRuntimeAsync(cts.Token);
                var value = await ResolveCredentialForPrintAsync(options, runtime, command.Kind, command.MinExpiryMs, cts.Token);
                Console.Out.WriteLine(value);
                return 0;
            }

            var (provider, model) = ValidateArgs(options, AuthCommandKind.Check);
            var credentials = command.NoRefresh
                ? new ReadOnlyCredentialStore(FileCredentialStore.CreateDefault())
                : (ICredentialStore)FileCredentialStore.CreateDefault();
            var result = await CheckProviderAuthAsync(options, await CreateCheckRuntimeAsync(credentials), command.NoRefresh);
            string? credential = null;
            if (command.Credentials && result.Status == "ready")
            {
                credential = await GetProviderCredentialAsync(result.Provider, command.NoRefresh);
                if (credential is null)
                {
                    result = result with { Status = "not_ready", Reason = "credential_not_available" };
                }
            }

            Console.Out.WriteLine(command.Json ? RenderJson(result, credential) : credential ?? result.Status);
            return result.Status == "ready" ? 0 : result.Status == "not_ready" ? 1 : 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AuthCommandError error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return command.Kind == AuthCommandKind.Check ? 2 : 1;
        }
        catch (Exception) when (command.Kind == AuthCommandKind.Check)
        {
            // Pinned check maps every unexpected failure to an invalid_state result.
            Console.Out.WriteLine(command.Json
                ? RenderJson(new AuthCheckResult("invalid", options.Provider ?? options.Model ?? string.Empty), null)
                : "invalid");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }
    }

    private static async Task<ModelRuntime> CreatePrintRuntimeAsync(CancellationToken cancellationToken)
    {
        var modelsJson = Path.Combine(ModelStartup.GetAgentDirectory(), "models.json");
        return await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            ModelsPath = File.Exists(modelsJson) ? modelsJson : null,
            AllowModelNetwork = false,
            RefreshOnCreate = false,
            Signal = cancellationToken,
        });
    }

    private static Task<ModelRuntime> CreateCheckRuntimeAsync(ICredentialStore credentials)
    {
        var modelsJson = Path.Combine(ModelStartup.GetAgentDirectory(), "models.json");
        return ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = credentials,
            ModelsStore = new InMemoryModelsStore(),
            ModelsPath = File.Exists(modelsJson) ? modelsJson : null,
            AllowModelNetwork = false,
            RefreshOnCreate = false,
        });
    }

    /// <summary>Result of auth check (pinned AuthCheckResult, JSON field order preserved).</summary>
    internal sealed record AuthCheckResult(
        string Status,
        string Provider,
        string? Reason = null,
        string? AuthType = null);

    /// <summary>
    /// Renders the pinned JSON field order (status, provider, reason?, authType?,
    /// credentials?) with the same quoting as JSON.stringify.
    /// </summary>
    private static string RenderJson(AuthCheckResult result, string? credential)
    {
        var fields = new List<string>
        {
            $"\"status\":{JsonString(result.Status)}",
            $"\"provider\":{JsonString(result.Provider)}",
        };
        if (result.Reason is not null)
        {
            fields.Add($"\"reason\":{JsonString(result.Reason)}");
        }

        if (result.AuthType is not null)
        {
            fields.Add($"\"authType\":{JsonString(result.AuthType)}");
        }

        if (credential is not null)
        {
            fields.Add($"\"credentials\":{JsonString(credential)}");
        }

        return "{" + string.Join(",", fields) + "}";
    }

    private static string JsonString(string value) => JsonSerializer.Serialize(value);

    /// <summary>Pinned checkProviderAuth.</summary>
    private static async Task<AuthCheckResult> CheckProviderAuthAsync(CliOptions options, ModelRuntime runtime, bool noRefresh)
    {
        var (cliProvider, cliModel) = ValidateArgs(options, AuthCommandKind.Check);
        var provider = cliProvider;
        if (cliModel is not null)
        {
            var resolved = ModelResolver.ResolveCliModel(cliProvider, cliModel, null, runtime);
            if (resolved.Error is not null || resolved.Model is null)
            {
                throw new AuthCommandError(resolved.Error ?? $"Unable to resolve model \"{cliModel}\"");
            }

            provider = resolved.Model.Provider;
        }

        if (provider is null)
        {
            throw new AuthCommandError("Unable to resolve an auth provider");
        }

        if (runtime.GetError() is not null)
        {
            return new AuthCheckResult("invalid", provider, "invalid_state");
        }

        if (runtime.GetProvider(provider) is null)
        {
            return new AuthCheckResult("not_ready", provider, "provider_not_found");
        }

        try
        {
            var auth = await runtime.CheckAuthAsync(provider);
            if (auth is null)
            {
                return new AuthCheckResult("not_ready", provider, "credentials_not_configured");
            }

            if (!noRefresh && await runtime.GetAuthAsync(provider) is null)
            {
                return new AuthCheckResult("not_ready", provider, "credentials_not_configured");
            }

            return new AuthCheckResult("ready", provider, null, auth.Type);
        }
        catch
        {
            // Pinned: any resolution failure is an invalid state, not a crash.
            return new AuthCheckResult("invalid", provider, "invalid_state");
        }
    }

    /// <summary>Pinned getProviderCredential.</summary>
    private static async Task<string?> GetProviderCredentialAsync(string providerId, bool noRefresh)
    {
        var store = FileCredentialStore.CreateDefault();
        var credential = await store.ReadAsync(providerId);
        if (noRefresh && credential is OAuthCredential oauth)
        {
            return oauth.Access;
        }

        var runtime = await CreateCheckRuntimeAsync(store);
        return GetAuthCredential(await runtime.GetAuthAsync(providerId));
    }

    /// <summary>Extracts the printable credential from resolved auth (pinned getAuthCredential).</summary>
    public static string? GetAuthCredential(AuthResult? auth)
    {
        if (auth?.Auth.ApiKey is { Length: > 0 } apiKey)
        {
            return apiKey;
        }

        string? authorization = null;
        if (auth?.Auth.Headers is { } headers)
        {
            foreach (var pair in headers)
            {
                if (string.Equals(pair.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    authorization = pair.Value;
                    break;
                }
            }
        }

        if (authorization is null)
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            authorization, @"^Bearer\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Pinned resolveCredentialForPrint.</summary>
    private static async Task<string> ResolveCredentialForPrintAsync(
        CliOptions options,
        ModelRuntime runtime,
        AuthCommandKind kind,
        int? minExpiryMs,
        CancellationToken cancellationToken)
    {
        var (cliProvider, cliModel) = ValidateArgs(options, kind);
        var credentialTypes = (await runtime.ListCredentialsAsync(cancellationToken))
            .ToDictionary(info => info.ProviderId, info => info.Type, StringComparer.Ordinal);
        var providers = new List<(string Id, ModelInfo? Model)>();

        if (cliProvider is not null)
        {
            var provider = runtime.GetProvider(cliProvider);
            if (provider is null)
            {
                throw new AuthCommandError($"Unknown provider \"{cliProvider}\". Use --list-models to see available providers.");
            }

            if (cliModel is not null)
            {
                var resolved = ModelResolver.ResolveCliModel(provider.Id, cliModel, null, runtime);
                if (resolved.Error is not null || resolved.Model is null)
                {
                    throw new AuthCommandError(resolved.Error ?? "Unable to resolve the requested provider/model");
                }

                providers.Add((provider.Id, resolved.Model));
            }
            else
            {
                providers.Add((provider.Id, null));
            }
        }
        else
        {
            foreach (var provider in runtime.GetProviders())
            {
                if (!credentialTypes.ContainsKey(provider.Id))
                {
                    continue;
                }

                var resolved = ModelResolver.ResolveCliModel(provider.Id, cliModel, null, runtime);
                if (resolved.Model is not null &&
                    resolved.Error is null &&
                    (resolved.Warning is null || !resolved.Warning.Contains("Using custom model id")))
                {
                    providers.Add((provider.Id, resolved.Model));
                }
            }

            if (providers.Count == 0)
            {
                throw new AuthCommandError($"Model \"{cliModel}\" not found. Use --list-models to see available models.");
            }
        }

        var credentials = new List<(string ProviderId, string Value)>();
        foreach (var (providerId, model) in providers)
        {
            var type = credentialTypes[providerId];
            if (kind == AuthCommandKind.PrintApiKey && type == "oauth")
            {
                continue;
            }

            if (kind == AuthCommandKind.PrintBearerToken && type != "oauth")
            {
                continue;
            }

            var auth = model is not null
                ? await runtime.GetAuthAsync(model, overridesFor(kind, minExpiryMs), cancellationToken)
                : await runtime.GetAuthAsync(providerId, overridesFor(kind, minExpiryMs), cancellationToken);
            var value = GetAuthCredential(auth);
            if (value is not null)
            {
                credentials.Add((providerId, value));
            }
        }

        if (credentials.Count == 1)
        {
            return credentials[0].Value;
        }

        if (credentials.Count == 0)
        {
            var providerId = providers.Count > 0 ? providers[0].Id : null;
            var type = providerId is not null && credentialTypes.TryGetValue(providerId, out var t) ? t : null;
            if (cliProvider is not null && kind == AuthCommandKind.PrintApiKey && type == "oauth")
            {
                throw new AuthCommandError($"Provider \"{providerId}\" is configured with OAuth, not an API key");
            }

            if (cliProvider is not null && kind == AuthCommandKind.PrintBearerToken && type != "oauth")
            {
                throw new AuthCommandError($"Provider \"{providerId}\" is not configured with an OAuth bearer token");
            }

            throw new AuthCommandError(kind == AuthCommandKind.PrintApiKey
                ? "No usable API key is configured"
                : "No usable OAuth bearer token is configured");
        }

        throw new AuthCommandError(
            $"Multiple configured providers matched ({string.Join(", ", credentials.Select(c => c.ProviderId))}). Specify --provider.");
    }

    private static AuthResolutionOverrides? overridesFor(AuthCommandKind kind, int? minExpiryMs) =>
        kind == AuthCommandKind.PrintBearerToken
            ? new AuthResolutionOverrides { MinOAuthValidityMs = minExpiryMs ?? DefaultBearerTokenMinExpiryMs }
            : null;
}
