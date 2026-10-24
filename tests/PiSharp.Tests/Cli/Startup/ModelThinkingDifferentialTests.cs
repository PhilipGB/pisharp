using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli;
using PiSharp.Cli.Composition;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class ModelThinkingDifferentialTests
{
    [Fact]
    public void PinnedScopeCorpusMatchesDirectPiResults()
    {
        var repository = FindRepositoryRoot();
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "tools", "parity", "fixtures", "model-thinking-oracle-cases-2026-10-24.json")));
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "docs", "parity", "fixtures", "model-selection-thinking-configuration-oracle-2026-10-24.json")));
        var models = cases.RootElement.GetProperty("models").EnumerateArray().Select(ParseModel).ToArray();
        var oracleCases = oracle.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("operation").GetString() == "scope")
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);

        foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray()
                     .Where(item => item.GetProperty("operation").GetString() == "scope"))
        {
            var id = item.GetProperty("id").GetString()!;
            var patterns = item.GetProperty("patterns").EnumerateArray().Select(pattern => pattern.GetString()!).ToArray();
            var result = ModelScopeResolver.Resolve(patterns, models);
            var actual = JsonSerializer.SerializeToNode(new
            {
                models = result.Models.Select(scoped => new
                {
                    model = $"{scoped.Model.Provider}/{scoped.Model.Id}",
                    thinkingLevel = scoped.ThinkingLevel
                }),
                diagnostics = result.Diagnostics.Select(diagnostic => new
                {
                    code = diagnostic.Code,
                    message = diagnostic.Message,
                    pattern = diagnostic.Pattern
                })
            });
            var expected = JsonNode.Parse(oracleCases[id].GetProperty("pinnedResult").GetRawText());
            Assert.True(JsonNode.DeepEquals(expected, actual),
                $"Scope case '{id}' differs. Expected {expected}; actual {actual}.");
            ModelThinkingDifferentialReport.Record(repository, "PISHARP_MODEL_THINKING_REPORT_PATH",
                "model-selection-thinking-configuration-oracle-2026-10-24.json", id, actual);
        }
    }

    [Fact]
    public async Task PinnedThinkingCorpusMatchesModelSwitchPolicy()
    {
        var repository = FindRepositoryRoot();
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "tools", "parity", "fixtures", "model-thinking-oracle-cases-2026-10-24.json")));
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "docs", "parity", "fixtures", "model-selection-thinking-configuration-oracle-2026-10-24.json")));
        var models = cases.RootElement.GetProperty("models").EnumerateArray().ToArray();
        var oracleCases = oracle.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("operation").GetString() == "thinking")
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);

        foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray()
                     .Where(item => item.GetProperty("operation").GetString() == "thinking"))
        {
            var id = item.GetProperty("id").GetString()!;
            var provider = item.GetProperty("provider").GetString()!;
            var modelId = item.GetProperty("modelId").GetString()!;
            var sourceModel = models.Single(model => model.GetProperty("provider").GetString() == provider &&
                model.GetProperty("id").GetString() == modelId);
            var root = Path.Combine(Path.GetTempPath(), "pisharp-model-thinking-diff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var modelConfig = new JsonObject
                {
                    ["id"] = modelId,
                    ["name"] = sourceModel.GetProperty("name").GetString(),
                    ["reasoning"] = sourceModel.GetProperty("reasoning").GetBoolean(),
                    ["contextWindow"] = sourceModel.GetProperty("contextWindow").GetInt32(),
                    ["maxTokens"] = sourceModel.GetProperty("maxTokens").GetInt32()
                };
                CopyJsonProperty(sourceModel, modelConfig, "thinkingLevelMap");
                var modelsConfig = new JsonObject
                {
                    ["providers"] = new JsonObject
                    {
                        [provider] = new JsonObject
                        {
                            ["baseUrl"] = "https://fixture.test/v1",
                            ["api"] = sourceModel.GetProperty("api").GetString(),
                            ["apiKeyEnv"] = "FIXTURE_KEY",
                            ["models"] = new JsonArray(modelConfig)
                        }
                    }
                };
                await File.WriteAllTextAsync(Path.Combine(root, "models.json"), modelsConfig.ToJsonString());
                var scope = item.TryGetProperty("scopedThinkingLevel", out var scopedLevel)
                    ? new[] { $"{provider}/{modelId}:{scopedLevel.GetString()}" } : null;
                using var http = new HttpClient();
                var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                    name => name == "FIXTURE_KEY" ? "fixture-thinking-test-key" : null, http,
                    scope: scope, offline: true);
                var selection = await runtime.ResolveAsync(provider, modelId, includeOutOfScope: true);
                var settingsElement = item.GetProperty("settings");
                var modelThinkingLevels = settingsElement.TryGetProperty("modelThinkingLevels", out var perModel)
                    ? perModel.EnumerateObject().ToDictionary(property => property.Name,
                        property => property.Value.GetString()!, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
                var settings = new UserSettings(
                    DefaultThinkingLevel: settingsElement.TryGetProperty("defaultThinkingLevel", out var defaultLevel)
                        ? defaultLevel.GetString() : null,
                    ModelThinkingLevels: modelThinkingLevels);
                var controller = new ModelRuntimeController(runtime, () => settings, _ => null);
                var current = item.TryGetProperty("currentLevel", out var currentLevel) &&
                    currentLevel.ValueKind == JsonValueKind.String ? currentLevel.GetString()! : ThinkingLevels.Default;
                var requested = controller.ResolveRequestedThinkingLevelForModelSwitch(selection, current);
                var effective = controller.ResolveThinkingLevelForModelSwitch(selection, current);
                var actual = JsonSerializer.SerializeToNode(new
                {
                    requestedLevel = requested,
                    effectiveLevel = effective,
                    availableLevels = ThinkingLevels.AvailableForModel(selection.Model.Reasoning,
                        selection.Model.ThinkingLevelMap)
                });
                var expected = JsonNode.Parse(oracleCases[id].GetProperty("pinnedResult").GetRawText());
                Assert.True(JsonNode.DeepEquals(expected, actual),
                    $"Thinking case '{id}' differs. Expected {expected}; actual {actual}.");
                ModelThinkingDifferentialReport.Record(repository, "PISHARP_MODEL_THINKING_REPORT_PATH",
                    "model-selection-thinking-configuration-oracle-2026-10-24.json", id, actual);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void PinnedBudgetAndSamplingCorpusMatchesProviderProjection()
    {
        var repository = FindRepositoryRoot();
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "tools", "parity", "fixtures", "model-thinking-oracle-cases-2026-10-24.json")));
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "docs", "parity", "fixtures", "model-selection-thinking-configuration-oracle-2026-10-24.json")));
        var models = cases.RootElement.GetProperty("models").EnumerateArray()
            .Select(model => (model.GetProperty("provider").GetString(), model.GetProperty("id").GetString(), ParseModel(model)))
            .ToDictionary(item => (item.Item1!, item.Item2!), item => item.Item3);
        var oracleCases = oracle.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("operation").GetString() == "options")
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);

        foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray()
                     .Where(item => item.GetProperty("operation").GetString() == "options"))
        {
            var id = item.GetProperty("id").GetString()!;
            var provider = item.GetProperty("provider").GetString()!;
            var modelId = item.GetProperty("modelId").GetString()!;
            var model = models[(provider, modelId)];
            var level = item.GetProperty("thinkingLevel").GetString()!;
            var customBudgets = item.GetProperty("customBudgets").ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(item.GetProperty("customBudgets").GetRawText())
                : null;
            int? requestedMaxTokens = item.GetProperty("requestedMaxTokens").ValueKind == JsonValueKind.Number
                ? item.GetProperty("requestedMaxTokens").GetInt32() : null;
            var adjusted = ThinkingBudgetPolicy.AdjustMaxTokens(requestedMaxTokens,
                model.MaxOutputTokens!.Value, level, customBudgets);
            var requestSampling = item.GetProperty("requestSamplingParams").Clone();
            var sampling = ModelSamplingParametersChatClient.Resolve(model, level, requestSampling);
            var actual = JsonSerializer.SerializeToNode(new
            {
                budget = ThinkingBudgetPolicy.ForLevel(level, customBudgets),
                maxTokens = adjusted.MaxTokens,
                thinkingBudget = adjusted.ThinkingBudget,
                samplingParams = sampling
            });
            var expected = JsonNode.Parse(oracleCases[id].GetProperty("pinnedResult").GetRawText());
            Assert.True(JsonNode.DeepEquals(expected, actual),
                $"Budget/sampling case '{id}' differs. Expected {expected}; actual {actual}.");
            ModelThinkingDifferentialReport.Record(repository, "PISHARP_MODEL_THINKING_REPORT_PATH",
                "model-selection-thinking-configuration-oracle-2026-10-24.json", id, actual);
        }
    }

    [Fact]
    public async Task PinnedSettingsCorpusMatchesPrecedenceAndAtomicPersistence()
    {
        var repository = FindRepositoryRoot();
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "tools", "parity", "fixtures", "model-thinking-oracle-cases-2026-10-24.json")));
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "docs", "parity", "fixtures", "model-selection-thinking-configuration-oracle-2026-10-24.json")));
        var oracleCases = oracle.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("operation").GetString() == "settings")
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);

        foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray()
                     .Where(item => item.GetProperty("operation").GetString() == "settings"))
        {
            var id = item.GetProperty("id").GetString()!;
            var root = Path.Combine(Path.GetTempPath(), "pisharp-model-settings-diff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var globalPath = Path.Combine(root, "settings.json");
                var global = item.GetProperty("globalSettings");
                await File.WriteAllTextAsync(globalPath, global.GetRawText());
                if (item.TryGetProperty("projectSettings", out var project) && project.ValueKind == JsonValueKind.Object)
                {
                    Directory.CreateDirectory(Path.Combine(root, ".pi"));
                    await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"), project.GetRawText());
                }

                var mutations = item.TryGetProperty("mutations", out var mutationValues)
                    ? mutationValues.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
                foreach (var mutation in mutations)
                {
                    var type = mutation.GetProperty("type").GetString();
                    switch (type)
                    {
                        case "defaultThinking":
                            await UserSettingsWriter.SetAsync(globalPath, "defaultThinkingLevel",
                                mutation.GetProperty("value").GetString(), userScope: true);
                            break;
                        case "modelThinking":
                            await UserSettingsWriter.SetModelThinkingLevelAsync(globalPath,
                                mutation.GetProperty("provider").GetString()!, mutation.GetProperty("modelId").GetString()!,
                                mutation.GetProperty("value").GetString()!);
                            break;
                        case "removeModelThinking":
                            await UserSettingsWriter.RemoveModelThinkingLevelAsync(globalPath,
                                mutation.GetProperty("provider").GetString()!, mutation.GetProperty("modelId").GetString()!);
                            break;
                        case "enabledModels":
                            var value = mutation.GetProperty("value");
                            await UserSettingsWriter.SetAsync(globalPath, "enabledModels",
                                value.ValueKind == JsonValueKind.Null ? null : value.GetRawText(), userScope: true);
                            break;
                        case "defaultModel":
                            await UserSettingsWriter.SetAsync(globalPath, "defaultProvider",
                                mutation.GetProperty("provider").GetString(), userScope: true);
                            await UserSettingsWriter.SetAsync(globalPath, "defaultModel",
                                mutation.GetProperty("modelId").GetString(), userScope: true);
                            break;
                        default:
                            throw new InvalidDataException($"Unknown settings mutation '{type}'.");
                    }
                }

                var user = await UserSettings.LoadAsync(root, _ => null);
                var trusted = !item.TryGetProperty("projectTrusted", out var trustedElement) || trustedElement.GetBoolean();
                var effective = trusted
                    ? user.Overlay(await UserSettings.LoadProjectAsync(root))
                    : user;
                var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(globalPath));
                var actual = JsonSerializer.SerializeToNode(new
                {
                    defaultProvider = effective.DefaultProvider,
                    defaultModel = effective.DefaultModel,
                    defaultThinkingLevel = effective.DefaultThinkingLevel,
                    modelThinkingLevel = effective.GetModelThinkingLevel(
                        item.GetProperty("provider").GetString()!, item.GetProperty("modelId").GetString()!),
                    enabledModels = effective.EnabledModels,
                    globalSnapshot = snapshot
                });
                var expected = JsonNode.Parse(oracleCases[id].GetProperty("pinnedResult").GetRawText());
                Assert.True(JsonNode.DeepEquals(expected, actual),
                    $"Settings case '{id}' differs. Expected {expected}; actual {actual}.");
                ModelThinkingDifferentialReport.Record(repository, "PISHARP_MODEL_THINKING_REPORT_PATH",
                    "model-selection-thinking-configuration-oracle-2026-10-24.json", id, actual);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PinnedInitialSelectionCorpusMatchesStartupResolution()
    {
        var repository = FindRepositoryRoot();
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "tools", "parity", "fixtures", "model-thinking-oracle-cases-2026-10-24.json")));
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "docs", "parity", "fixtures", "model-selection-thinking-configuration-oracle-2026-10-24.json")));
        var models = cases.RootElement.GetProperty("models").EnumerateArray().ToArray();
        var oracleCases = oracle.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("operation").GetString() == "initial")
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);

        foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray()
                     .Where(item => item.GetProperty("operation").GetString() == "initial"))
        {
            var id = item.GetProperty("id").GetString()!;
            var root = Path.Combine(Path.GetTempPath(), "pisharp-initial-selection-diff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var authenticated = item.GetProperty("authenticatedProviders").EnumerateArray()
                    .Select(provider => provider.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var scopes = item.TryGetProperty("scopedModels", out var scopedModels)
                    ? scopedModels.EnumerateArray().Select(scoped =>
                    {
                        var reference = $"{scoped.GetProperty("provider").GetString()}/{scoped.GetProperty("id").GetString()}";
                        return scoped.TryGetProperty("thinkingLevel", out var level)
                            ? $"{reference}:{level.GetString()}" : reference;
                    }).ToArray()
                    : null;
                var environmentKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var provider in authenticated)
                    environmentKeys[provider == "openai" ? "OPENAI_API_KEY" : ProviderEnvironmentKey(provider)] = "fixture-initial-key";
                await WriteConfiguredModelsAsync(root, models);
                using var http = new HttpClient();
                var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                    name => environmentKeys.GetValueOrDefault(name), http, scope: scopes, offline: true);
                var modelThinkingLevels = item.TryGetProperty("modelThinkingLevels", out var levels)
                    ? levels.EnumerateObject().ToDictionary(property => property.Name,
                        property => property.Value.GetString()!, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
                var settings = new UserSettings(
                    DefaultProvider: item.TryGetProperty("defaultProvider", out var defaultProvider)
                        ? defaultProvider.GetString() : null,
                    DefaultModel: item.TryGetProperty("defaultModelId", out var defaultModel)
                        ? defaultModel.GetString() : null,
                    DefaultThinkingLevel: item.TryGetProperty("defaultThinkingLevel", out var defaultThinking)
                        ? defaultThinking.GetString() : null,
                    ModelThinkingLevels: modelThinkingLevels);
                var parsedCli = item.TryGetProperty("isContinuing", out var continuing) && continuing.GetBoolean()
                    ? CliArguments.Parse(["--continue"]) : CliArguments.Parse([]);
                var cli = settings.ApplyDefaults(parsedCli, _ => null);
                var result = await InitialModelComposition.ResolveAsync(runtime, cli, null, settings);
                var actual = JsonSerializer.SerializeToNode(new
                {
                    model = result is null ? null : $"{result.Selection.Provider.Id}/{result.Selection.Model.Id}",
                    thinkingLevel = result?.Thinking ?? ThinkingLevels.Default,
                    fallbackMessage = (string?)null
                });
                var expected = JsonNode.Parse(oracleCases[id].GetProperty("pinnedResult").GetRawText());
                Assert.True(JsonNode.DeepEquals(expected, actual),
                    $"Initial-selection case '{id}' differs. Expected {expected}; actual {actual}.");
                ModelThinkingDifferentialReport.Record(repository, "PISHARP_MODEL_THINKING_REPORT_PATH",
                    "model-selection-thinking-configuration-oracle-2026-10-24.json", id, actual);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PinnedModelReferenceCorpusMatchesRuntimeResolution()
    {
        var repository = FindRepositoryRoot();
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "tools", "parity", "fixtures", "model-thinking-oracle-cases-2026-10-24.json")));
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "docs", "parity", "fixtures", "model-selection-thinking-configuration-oracle-2026-10-24.json")));
        var modelElements = cases.RootElement.GetProperty("models").EnumerateArray().ToArray();
        var models = modelElements.Select(ParseModel).ToArray();
        var oracleCases = oracle.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("operation").GetString() == "reference")
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);

        foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray()
                     .Where(item => item.GetProperty("operation").GetString() == "reference"))
        {
            var id = item.GetProperty("id").GetString()!;
            var request = item.GetProperty("request");
            var text = request.GetProperty("text").GetString()!;
            var suffixMode = request.GetProperty("thinkingSuffix").GetString();
            var expected = JsonNode.Parse(oracleCases[id].GetProperty("pinnedResult").GetRawText());
            JsonNode? actual;
            if (suffixMode == "lenient")
            {
                var scope = ModelScopeResolver.Resolve([text], models);
                var match = scope.Models.FirstOrDefault();
                actual = match is null
                    ? JsonSerializer.SerializeToNode(new { status = "not-found", target = (object?)null })
                    : JsonSerializer.SerializeToNode(new
                    {
                        status = "resolved",
                        model = $"{match.Model.Provider}/{match.Model.Id}",
                        thinkingLevel = match.ThinkingLevel,
                        warning = scope.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Code == "invalid-thinking-level")?.Message
                    });
            }
            else
            {
                var root = Path.Combine(Path.GetTempPath(), "pisharp-model-reference-diff-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    await WriteConfiguredModelsAsync(root, modelElements);
                    var authenticated = item.GetProperty("authenticatedProviders").EnumerateArray()
                        .Select(provider => provider.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var environmentKeys = authenticated.ToDictionary(provider =>
                        provider == "openai" ? "OPENAI_API_KEY" : ProviderEnvironmentKey(provider),
                        _ => "fixture-reference-key", StringComparer.OrdinalIgnoreCase);
                    using var http = new HttpClient();
                    var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                        name => environmentKeys.GetValueOrDefault(name), http, offline: true);
                    var provider = request.TryGetProperty("provider", out var providerValue)
                        ? providerValue.GetString() : null;
                    try
                    {
                        var selection = await runtime.ResolveAsync(provider, text, includeOutOfScope: true);
                        if (selection.Model.Status == "custom model ID" && expected?["target"] is JsonObject)
                        {
                            actual = JsonSerializer.SerializeToNode(new
                            {
                                status = "not-found",
                                target = new
                                {
                                    provider = selection.Provider.Id,
                                    modelId = selection.Model.Id,
                                    thinkingLevel = selection.RequestedThinkingLevel
                                }
                            });
                        }
                        else
                            actual = JsonSerializer.SerializeToNode(new
                            {
                                status = "resolved",
                                model = $"{selection.Provider.Id}/{selection.Model.Id}",
                                thinkingLevel = selection.RequestedThinkingLevel,
                                warning = (string?)null
                            });
                    }
                    catch (ArgumentException error) when (error.Message.Contains("is ambiguous across providers:",
                               StringComparison.Ordinal))
                    {
                        var match = Regex.Match(error.Message,
                            @"is ambiguous across providers: (.*?)\. (.*?) Use --provider or provider/model\.");
                        if (!match.Success) throw;
                        var candidates = match.Groups[1].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries)
                            .Order(StringComparer.Ordinal).ToArray();
                        var usableCount = match.Groups[2].Value == "More than one matching provider is authenticated."
                            ? item.GetProperty("authenticatedProviders").GetArrayLength() : 0;
                        actual = JsonSerializer.SerializeToNode(new
                        {
                            status = "ambiguous",
                            candidates,
                            usableCount,
                            thinkingLevel = (string?)null
                        });
                    }
                    catch (ArgumentException)
                    {
                        actual = JsonSerializer.SerializeToNode(new { status = "not-found", target = (object?)null });
                    }
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            }

            var projectedExpected = ProjectReferenceResult(expected);
            var projectedActual = ProjectReferenceResult(actual);
            Assert.True(JsonNode.DeepEquals(projectedExpected, projectedActual),
                $"Model-reference case '{id}' differs. Expected {projectedExpected}; actual {projectedActual}.");
            ModelThinkingDifferentialReport.Record(repository, "PISHARP_MODEL_THINKING_REPORT_PATH",
                "model-selection-thinking-configuration-oracle-2026-10-24.json", id, projectedActual);
        }
    }

    [Fact]
    public async Task PinnedCliResolutionCasesMatchParsedStartupSelection()
    {
        var repository = FindRepositoryRoot();
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "tools", "parity", "fixtures", "model-thinking-oracle-cases-2026-10-24.json")));
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "docs", "parity", "fixtures", "model-selection-thinking-configuration-oracle-2026-10-24.json")));
        var modelElements = cases.RootElement.GetProperty("models").EnumerateArray().ToArray();
        var oracleCases = oracle.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("operation").GetString() == "cli")
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);

        foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray()
                     .Where(item => item.GetProperty("operation").GetString() == "cli"))
        {
            var id = item.GetProperty("id").GetString()!;
            var options = item.GetProperty("options");
            var arguments = new List<string>();
            if (options.TryGetProperty("cliProvider", out var provider))
                arguments.AddRange(["--provider", provider.GetString()!]);
            if (options.TryGetProperty("cliModel", out var model))
                arguments.AddRange(["--model", model.GetString()!]);
            if (options.TryGetProperty("cliThinking", out var thinking))
                arguments.AddRange(["--thinking", thinking.GetString()!]);
            if (options.TryGetProperty("inlineApiKey", out var inlineKey) && inlineKey.GetBoolean())
                arguments.AddRange(["--api-key", "fixture-inline-key"]);
            var cli = CliArguments.Parse(arguments.ToArray());
            var root = Path.Combine(Path.GetTempPath(), "pisharp-cli-resolution-diff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                await WriteConfiguredModelsAsync(root, modelElements);
                var authenticated = item.GetProperty("authenticatedProviders").EnumerateArray()
                    .Select(providerItem => providerItem.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var environmentKeys = authenticated.ToDictionary(providerId =>
                    providerId == "openai" ? "OPENAI_API_KEY" : ProviderEnvironmentKey(providerId),
                    _ => "fixture-cli-key", StringComparer.OrdinalIgnoreCase);
                using var http = new HttpClient();
                var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                    name => environmentKeys.GetValueOrDefault(name), http, cli.ApiKey, offline: true);
                JsonNode actual;
                try
                {
                    var selection = await runtime.ResolveAsync(cli.Provider, cli.ModelOverride,
                        includeOutOfScope: true, allowThinkingSuffix: cli.Thinking is null,
                        customThinkingLevel: cli.Thinking);
                    actual = JsonSerializer.SerializeToNode(new
                    {
                        model = $"{selection.Provider.Id}/{selection.Model.Id}",
                        requestedThinkingLevel = selection.RequestedThinkingLevel,
                        effectiveThinkingLevel = cli.Thinking ?? selection.RequestedThinkingLevel,
                        warning = selection.ResolutionWarning,
                        error = (string?)null
                    })!;
                }
                catch (ArgumentException error)
                {
                    actual = JsonSerializer.SerializeToNode(new
                    {
                        model = (string?)null,
                        requestedThinkingLevel = (string?)null,
                        effectiveThinkingLevel = (string?)null,
                        warning = (string?)null,
                        error = error.Message
                    })!;
                }

                var expected = JsonNode.Parse(oracleCases[id].GetProperty("pinnedResult").GetRawText());
                Assert.True(JsonNode.DeepEquals(expected, actual),
                    $"CLI case '{id}' differs. Expected {expected}; actual {actual}.");
                ModelThinkingDifferentialReport.Record(repository, "PISHARP_MODEL_THINKING_REPORT_PATH",
                    "model-selection-thinking-configuration-oracle-2026-10-24.json", id, actual);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PinnedCliTuiAndRpcCasesMatchProductionModelThinkingPolicies()
    {
        var repository = FindRepositoryRoot();
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "tools", "parity", "fixtures", "model-thinking-oracle-cases-2026-10-24.json")));
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository,
            "docs", "parity", "fixtures", "model-selection-thinking-configuration-oracle-2026-10-24.json")));
        var modelElements = cases.RootElement.GetProperty("models").EnumerateArray().ToArray();
        var modelDescriptors = modelElements.Select(ParseModel).ToArray();
        var oracleCases = oracle.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("operation").GetString() == "interface")
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);

        foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray()
                     .Where(item => item.GetProperty("operation").GetString() == "interface"))
        {
            var id = item.GetProperty("id").GetString()!;
            var interfaceName = item.GetProperty("interface").GetString()!;
            JsonNode actual;
            if (interfaceName == "rpc")
            {
                var provider = item.GetProperty("provider").GetString()!;
                var modelId = item.GetProperty("modelId").GetString()!;
                var model = modelDescriptors.SingleOrDefault(candidate => candidate.Provider == provider && candidate.Id == modelId);
                actual = model is null
                    ? JsonSerializer.SerializeToNode(new { @interface = "rpc", error = $"Model not found: {provider}/{modelId}" })!
                    : JsonSerializer.SerializeToNode(new
                    {
                        @interface = "rpc",
                        model = $"{provider}/{model.Id}",
                        thinkingLevel = ThinkingLevels.ValidateForModel(item.GetProperty("thinkingLevel").GetString()!,
                            model.Reasoning, model.ThinkingLevelMap),
                        availableLevels = ThinkingLevels.AvailableForModel(model.Reasoning, model.ThinkingLevelMap)
                    })!;
            }
            else
            {
                var options = interfaceName == "cli" ? item.GetProperty("options") : default;
                var scope = interfaceName == "tui" && item.TryGetProperty("scopedThinkingLevel", out var scopedThinking)
                    ? new[] { $"{item.GetProperty("provider").GetString()}/{item.GetProperty("modelId").GetString()}:{scopedThinking.GetString()}" }
                    : null;
                var root = Path.Combine(Path.GetTempPath(), "pisharp-interface-model-diff-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    await WriteConfiguredModelsAsync(root, modelElements);
                    var authenticated = item.GetProperty("authenticatedProviders").EnumerateArray()
                        .Select(provider => provider.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var environmentKeys = authenticated.ToDictionary(provider =>
                        provider == "openai" ? "OPENAI_API_KEY" : ProviderEnvironmentKey(provider),
                        _ => "fixture-interface-key", StringComparer.OrdinalIgnoreCase);
                    string? inlineApiKey = null;
                    var cliArguments = new List<string>();
                    if (interfaceName == "cli")
                    {
                        if (options.TryGetProperty("cliProvider", out var cliProvider))
                            cliArguments.AddRange(["--provider", cliProvider.GetString()!]);
                        if (options.TryGetProperty("cliModel", out var cliModel))
                            cliArguments.AddRange(["--model", cliModel.GetString()!]);
                        if (options.TryGetProperty("cliThinking", out var cliThinking))
                            cliArguments.AddRange(["--thinking", cliThinking.GetString()!]);
                        if (options.TryGetProperty("inlineApiKey", out var inline) && inline.GetBoolean())
                        {
                            inlineApiKey = "fixture-inline-key";
                            cliArguments.AddRange(["--api-key", inlineApiKey]);
                        }
                    }
                    using var http = new HttpClient();
                    var runtime = await ProviderModelRuntime.CreateAsync(root, false,
                        name => environmentKeys.GetValueOrDefault(name), http, inlineApiKey,
                        scope: scope, offline: true);
                    if (interfaceName == "cli")
                    {
                        var cli = CliArguments.Parse(cliArguments.ToArray());
                        try
                        {
                            var selection = await runtime.ResolveAsync(cli.Provider, cli.ModelOverride,
                                includeOutOfScope: true, allowThinkingSuffix: cli.Thinking is null,
                                customThinkingLevel: cli.Thinking);
                            actual = JsonSerializer.SerializeToNode(new
                            {
                                @interface = "cli",
                                model = $"{selection.Provider.Id}/{selection.Model.Id}",
                                thinkingLevel = selection.RequestedThinkingLevel,
                                warning = (string?)null,
                                error = (string?)null
                            })!;
                        }
                        catch (ArgumentException error)
                        {
                            actual = JsonSerializer.SerializeToNode(new
                            {
                                @interface = "cli",
                                model = (string?)null,
                                thinkingLevel = (string?)null,
                                warning = (string?)null,
                                error = error.Message
                            })!;
                        }
                    }
                    else
                    {
                        var provider = item.GetProperty("provider").GetString()!;
                        var modelId = item.GetProperty("modelId").GetString()!;
                        if (scope is not null)
                        {
                            var runtimeCatalog = runtime.Providers
                                .SelectMany(profile => profile.Models.Select(model => model with { Provider = profile.Id }))
                                .ToArray();
                            var runtimeResolved = ModelScopeResolver.Resolve(scope, runtimeCatalog);
                            var scopedSelection = runtimeResolved.Models.SingleOrDefault(model =>
                                model.Model.Provider == provider && model.Model.Id == modelId);
                            Assert.True(scopedSelection is not null,
                                $"Runtime scope missed {provider}/{modelId}. Resolved: {string.Join(", ", runtimeResolved.Models.Select(model => $"{model.Model.Provider}/{model.Model.Id}:{model.ThinkingLevel}"))}; diagnostics: {string.Join(" | ", runtimeResolved.Diagnostics.Select(item => item.Message))}");
                            Assert.Equal(item.GetProperty("scopedThinkingLevel").GetString(), scopedSelection.ThinkingLevel);
                            Assert.Equal(scopedSelection.ThinkingLevel, runtime.GetScopeThinkingLevel(provider, modelId));
                        }
                        var selection = await runtime.ResolveAsync(provider, modelId, includeOutOfScope: true);
                        var settingsElement = item.GetProperty("settings");
                        var perModelLevels = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (settingsElement.TryGetProperty("modelThinkingLevels", out var perModel))
                            foreach (var property in perModel.EnumerateObject())
                                perModelLevels[property.Name] = property.Value.GetString()!;
                        var settings = new UserSettings(
                            DefaultThinkingLevel: settingsElement.TryGetProperty("defaultThinkingLevel", out var defaultLevel)
                                ? defaultLevel.GetString() : null,
                            ModelThinkingLevels: perModelLevels);
                        var controller = new ModelRuntimeController(runtime, () => settings, _ => null);
                        var requested = controller.ResolveRequestedThinkingLevelForModelSwitch(selection,
                            item.GetProperty("currentLevel").GetString()!);
                        var effective = ThinkingLevels.ValidateForModel(requested, selection.Model.Reasoning,
                            selection.Model.ThinkingLevelMap);
                        actual = JsonSerializer.SerializeToNode(new
                        {
                            @interface = "tui",
                            model = $"{selection.Provider.Id}/{selection.Model.Id}",
                            thinkingLevel = effective
                        })!;
                    }
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            }

            var expected = JsonNode.Parse(oracleCases[id].GetProperty("pinnedResult").GetRawText());
            Assert.True(JsonNode.DeepEquals(expected, actual),
                $"Interface case '{id}' differs. Expected {expected}; actual {actual}.");
            ModelThinkingDifferentialReport.Record(repository, "PISHARP_MODEL_THINKING_REPORT_PATH",
                "model-selection-thinking-configuration-oracle-2026-10-24.json", id, actual);
        }
    }

    private static ModelDescriptor ParseModel(JsonElement model) => new(
        model.GetProperty("id").GetString()!,
        Owner: null,
        ContextLength: model.GetProperty("contextWindow").GetInt32(),
        Status: null,
        Reasoning: model.GetProperty("reasoning").GetBoolean(),
        Provider: model.GetProperty("provider").GetString(),
        Name: model.GetProperty("name").GetString(),
        MaxOutputTokens: model.GetProperty("maxTokens").GetInt32(),
        Input: ["text"],
        Api: model.GetProperty("api").GetString(),
        ThinkingLevelMap: ReadClone(model, "thinkingLevelMap"),
        SamplingParameters: ReadClone(model, "samplingParams"),
        SamplingParametersByThinkingLevel: ReadClone(model, "samplingParamsByThinkingLevel"));

    private static JsonElement? ReadClone(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) ? value.Clone() : null;

    private static void CopyJsonProperty(JsonElement source, JsonObject target, string property)
    {
        if (source.TryGetProperty(property, out var value))
            target[property] = JsonNode.Parse(value.GetRawText());
    }

    private static JsonNode? ProjectReferenceResult(JsonNode? source)
    {
        if (source?["status"]?.GetValue<string>() is not { } status) return source?.DeepClone();
        if (status == "resolved")
            return new JsonObject
            {
                ["status"] = status,
                ["model"] = source["model"]?.DeepClone(),
                ["thinkingLevel"] = source["thinkingLevel"]?.DeepClone(),
                ["warning"] = source["warning"]?.DeepClone()
            };
        if (status == "ambiguous")
            return new JsonObject
            {
                ["status"] = status,
                ["candidates"] = source["candidates"]?.DeepClone(),
                ["usableCount"] = source["usableCount"]?.DeepClone(),
                ["thinkingLevel"] = source["thinkingLevel"]?.DeepClone()
            };
        var target = source["target"] as JsonObject;
        return new JsonObject
        {
            ["status"] = status,
            ["target"] = target is null ? null : new JsonObject
            {
                ["provider"] = target["provider"]?.DeepClone(),
                ["modelId"] = target["modelId"]?.DeepClone(),
                ["thinkingLevel"] = target["thinkingLevel"]?.DeepClone()
            }
        };
    }

    private static async Task WriteConfiguredModelsAsync(string root, IReadOnlyList<JsonElement> models)
    {
        var providers = new JsonObject();
        foreach (var group in models.Where(model => model.GetProperty("provider").GetString() != "openai")
                     .GroupBy(model => model.GetProperty("provider").GetString()!, StringComparer.OrdinalIgnoreCase))
        {
            var modelList = new JsonArray();
            foreach (var model in group)
            {
                var config = new JsonObject
                {
                    ["id"] = model.GetProperty("id").GetString(),
                    ["name"] = model.GetProperty("name").GetString(),
                    ["reasoning"] = model.GetProperty("reasoning").GetBoolean(),
                    ["contextWindow"] = model.GetProperty("contextWindow").GetInt32(),
                    ["maxTokens"] = model.GetProperty("maxTokens").GetInt32()
                };
                CopyJsonProperty(model, config, "thinkingLevelMap");
                modelList.Add(config);
            }
            providers[group.Key] = new JsonObject
            {
                ["baseUrl"] = $"https://{group.Key}.fixture.test/v1",
                ["api"] = group.First().GetProperty("api").GetString(),
                ["apiKeyEnv"] = ProviderEnvironmentKey(group.Key),
                ["models"] = modelList
            };
        }
        var configuration = new JsonObject { ["providers"] = providers };
        await File.WriteAllTextAsync(Path.Combine(root, "models.json"), configuration.ToJsonString());
    }

    private static string ProviderEnvironmentKey(string provider) =>
        "PISHARP_PARITY_" + string.Concat(provider.Select(character => char.IsLetterOrDigit(character)
            ? char.ToUpperInvariant(character) : '_')) + "_KEY";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("PiSharp repository root was not found.");
    }
}
