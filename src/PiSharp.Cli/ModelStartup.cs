using PiSharp.Core.Models;
using PiSharp.Core.Models.Auth;
using PiSharp.Core.Models.Providers;
using PiSharp.Core.Settings;

namespace PiSharp.Cli;

/// <summary>
/// Result of building the model runtime and resolving the startup model selection.
/// <see cref="Error"/> is set when the startup must abort (e.g. an unresolvable explicit
/// --model); the host reports it and exits non-zero (pinned main.ts reportDiagnostics).
/// </summary>
internal sealed record ModelStartupResult(
    ModelRuntime Runtime,
    ModelSessionState ModelState,
    IReadOnlyList<string> Warnings,
    string? Error);

/// <summary>
/// Owns model runtime construction and startup model resolution (pinned main.ts
/// model-runtime creation + resolveModelScope + buildSessionOptions, minus the session part
/// which lives in the session controller).
///
/// Runtime construction: built-in providers (with remote catalog refresh unless offline),
/// models.json from the agent directory, auth.json credentials, and the local endpoint
/// provider for the PISHARP_MODEL/PISHARP_ENDPOINT/--model/--endpoint workflow. The explicit
/// --api-key maps to a non-persistent runtime API key override (pinned setRuntimeApiKey).
/// </summary>
internal static class ModelStartup
{
    /// <summary>The agent directory: PISHARP_AGENT_DIR, falling back to ~/.pisharp.</summary>
    public static string GetAgentDirectory() =>
        Environment.GetEnvironmentVariable("PISHARP_AGENT_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pisharp");

    /// <summary>
    /// Builds the model runtime and resolves scoped models. The session model itself is
    /// resolved by the session controller once the active document is known (resume/continue
    /// can change the precedence inputs).
    /// </summary>
    public static async Task<ModelStartupResult> CreateAsync(
        CliOptions options,
        SettingsManager settings,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var agentDir = GetAgentDirectory();
        var modelsJsonPath = Path.Combine(agentDir, "models.json");

        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            // CreateModelRuntimeOptions.AuthPath is the agent directory; the credential
            // store appends auth.json itself.
            AuthPath = agentDir,
            ModelsPath = File.Exists(modelsJsonPath) ? modelsJsonPath : null,
            AllowModelNetwork = !options.Offline,
            RefreshOnCreate = true,
            NetworkEnabled = !options.Offline,
            ModelRefreshTimeoutMs = 15_000,
            Signal = cancellationToken,
        });

        if (runtime.GetError() is { } catalogError)
        {
            warnings.Add(catalogError);
        }

        // The local endpoint workflow: PISHARP_MODEL + PISHARP_ENDPOINT (or --model +
        // --endpoint) targets an OpenAI-compatible server directly. It registers a llama.cpp
        // provider with the raw model id, exactly like the legacy client construction.
        if (!string.IsNullOrWhiteSpace(options.Endpoint) && !string.IsNullOrWhiteSpace(options.Model))
        {
            runtime.RegisterProvider(BuiltinProviders.CreateLlamaCppProvider(
                options.Endpoint,
                options.Model,
                contextWindow: null,
                maxOutputTokens: options.MaxOutputTokens > 0 ? (int?)options.MaxOutputTokens : null,
                temperature: null));
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                await runtime.SetRuntimeApiKeyAsync(
                    BuiltinProviders.LlamaCppProviderId, options.ApiKey, cancellationToken);
            }
        }
        else if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            warnings.Add("--endpoint requires --model to target the local OpenAI-compatible server.");
        }

        // Explicit --api-key without an endpoint: a non-persistent runtime override for the
        // provider the CLI model resolves to (pinned setRuntimeApiKey). Resolution happens in
        // the session controller; here we only validate the flag's precondition (pinned:
        // "--api-key requires a model to be specified").
        if (!string.IsNullOrWhiteSpace(options.ApiKey) &&
            string.IsNullOrWhiteSpace(options.Endpoint) &&
            string.IsNullOrWhiteSpace(options.Model))
        {
            return new ModelStartupResult(
                runtime,
                CreateModelState(runtime, settings, options, null),
                warnings,
                "--api-key requires a model to be specified via --model, --provider/--model, or --models");
        }

        var scopedModels = await ResolveScopedModelsAsync(options, runtime, warnings, cancellationToken);
        var modelState = CreateModelState(runtime, settings, options, scopedModels);
        return new ModelStartupResult(runtime, modelState, warnings, null);
    }

    private static ModelSessionState CreateModelState(
        ModelRuntime runtime,
        SettingsManager settings,
        CliOptions options,
        IReadOnlyList<ScopedModel>? scopedModels)
    {
        // Session-level overrides (PiSharp extensions for local endpoints). Pinned Pi has no
        // context/max-output CLI flags and always follows the model metadata; explicit user
        // intent (--context-tokens / --max-output-tokens or their env vars) wins over model
        // metadata (documented difference) while still being re-evaluated per request after
        // every /model.
        var contextWindowOverride = options.ContextTokensExplicit ? (int?)options.ContextTokens : null;
        var maxOutputOverride = options.MaxOutputTokensExplicit ? (int?)options.MaxOutputTokens : null;
        return new ModelSessionState(
            runtime,
            settings,
            current: null,
            scopedModels: scopedModels,
            contextWindowOverride: contextWindowOverride,
            maxOutputOverride: maxOutputOverride);
    }

    private static async Task<IReadOnlyList<ScopedModel>> ResolveScopedModelsAsync(
        CliOptions options,
        ModelRuntime runtime,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (options.Models.Count == 0)
        {
            return [];
        }

        // Pinned resolves scopes against the available (authenticated) model set with a
        // 15s cap: on timeout it proceeds with the current snapshot instead of failing.
        ModelInfo[] available;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            available = (await runtime.GetAvailableAsync(cancellationToken: cts.Token)).ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            available = runtime.GetAvailableSnapshot().ToArray();
            warnings.Add("Model availability refresh timed out; continuing with the current snapshot.");
        }

        // Selection boundary (item 3): scopes resolve against executable models only; when
        // the authenticated catalogue also holds inexecutable models, say so.
        var excludedCount = available.Count(model => !ModelExecutionSupport.CanExecute(model));
        if (excludedCount > 0)
        {
            warnings.Add(
                $"{excludedCount} authenticated model(s) were excluded from model selection: this build can only execute models on API '{string.Join("', '", ModelExecutionSupport.SupportedApis)}'.");
            available = available.Where(ModelExecutionSupport.CanExecute).ToArray();
        }

        var scope = ModelResolver.ResolveModelScopeFromModels(options.Models, available);
        foreach (var diagnostic in scope.Diagnostics)
        {
            warnings.Add(diagnostic.Message);
        }

        return scope.ScopedModels;
    }
}
