using System.Text.RegularExpressions;
using PiSharp.Cli.Authentication;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalLlamaManager(ProviderModelRuntime runtime, TerminalEditor editor, HttpClient http,
    Func<string, string?> environment)
{
    private static readonly Regex s_repositoryInput = new("^[^/\\s]+/[^:\\s]+(?::[^\\s:]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task ShowAsync(string arguments, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(arguments)) throw new ArgumentException("Use /llama without arguments.");
        LlamaRouterClient client;
        try { client = await runtime.CreateLlamaRouterClientAsync(cancellationToken).ConfigureAwait(false); }
        catch (InvalidOperationException error) when (error.Message.Contains("LLAMA_BASE_URL", StringComparison.Ordinal))
        {
            Console.WriteLine("Configure llama.cpp with /login llama.cpp");
            return;
        }

        var catalog = await ReadCatalogAsync(client, cancellationToken).ConfigureAwait(false);
        if (catalog is null) return;
        while (true)
        {
            var options = ModelOptions(catalog);
            var selected = editor.ShowInlineSelectionList("llama.cpp models", options,
                [ServerLabel(client.ServerUrl), ""], "enter load/unload/download • escape/ctrl+c close");
            if (selected is null) return;
            Exception? actionError = null;
            try
            {
                var action = selected.Option.Value;
                if (action.Download) await DownloadModelAsync(client, cancellationToken).ConfigureAwait(false);
                else if (action.Model is { } model && IsLoaded(model))
                    await UnloadModelAsync(client, model, cancellationToken).ConfigureAwait(false);
                else if (action.Model is { Status.Value: "unloaded" } target)
                    await LoadModelAsync(client, catalog, target, cancellationToken).ConfigureAwait(false);
                else if (action.Model is { } inProgress)
                    Console.WriteLine($"{inProgress.Id} is {inProgress.Status.Value}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { actionError = error; }

            catalog = await ReadCatalogAsync(client, cancellationToken).ConfigureAwait(false);
            if (catalog is null) return;
            if (actionError is not null)
                Console.WriteLine(SecretRedactor.Redact(actionError.Message, environment("LLAMA_API_KEY")));
        }
    }

    private async Task<IReadOnlyList<LlamaRouterModelInfo>?> ReadCatalogAsync(LlamaRouterClient client,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                // Pi keeps the manager's initial listing as its view snapshot, then refreshes the provider
                // registry independently. That second request also refreshes selectable chat/classifier models.
                var managerSnapshot = await client.ListAsync(cancellationToken).ConfigureAwait(false);
                _ = await runtime.RefreshLlamaRouterCatalogAsync(client, cancellationToken).ConfigureAwait(false);
                return managerSnapshot;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                var message = error is HttpRequestException or TaskCanceledException
                    ? "Could not connect to the server." : error.Message;
                var choice = editor.ShowInlineSelectionList("llama.cpp unavailable", new[]
                {
                    new TerminalSelectionOption<string>("retry", "retry", "Retry"),
                    new TerminalSelectionOption<string>("close", "close", "Close")
                }, [ServerLabel(client.ServerUrl), "", message], "enter select • escape cancel");
                if (choice?.Option.Value != "retry") return null;
            }
        }
    }

    private static IReadOnlyList<TerminalSelectionOption<LlamaAction>> ModelOptions(
        IReadOnlyList<LlamaRouterModelInfo> catalog)
    {
        var sorted = catalog.OrderByDescending(model => model.Status.Value == "loaded")
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var options = sorted.Select(model => new TerminalSelectionOption<LlamaAction>(
            "model:" + model.Id, new(model, false), model.Id, Describe(model),
            model.Id + " " + model.Status.Value + " " + model.Source)).ToList();
        options.Add(new("download", new(null, true), "Download model…",
            "Hugging Face owner/repository[:quant]", "download hugging face gguf"));
        return options;
    }

    private static string? Describe(LlamaRouterModelInfo model)
    {
        var loaded = IsLoaded(model);
        var details = new List<string>();
        if (loaded) details.Add("loaded");
        else if (model.Status.Value != "unloaded") details.Add(model.Status.Value);
        if (loaded && ContextLabel(model) is { } context) details.Add(context + " context");
        return details.Count == 0 ? null : string.Join(" · ", details);
    }

    private static string? ContextLabel(LlamaRouterModelInfo model)
    {
        var context = model.Metadata?.ContextWindow ?? model.Metadata?.TrainingContextWindow;
        if (context is > 0) return context >= 1000 ? $"{Math.Round(context.Value / 1000d):0}k" : context.Value.ToString();
        var args = model.Status.Args ?? [];
        for (var index = 0; index + 1 < args.Count; index++)
        {
            if (args[index] is not ("--ctx-size" or "-c" or "-ctx") ||
                !int.TryParse(args[index + 1], out var configured) || configured <= 0) continue;
            return configured >= 1000 ? $"{Math.Round(configured / 1000d):0}k" : configured.ToString();
        }
        return null;
    }

    private async Task LoadModelAsync(LlamaRouterClient client, IReadOnlyList<LlamaRouterModelInfo> catalog,
        LlamaRouterModelInfo target, CancellationToken cancellationToken)
    {
        var loaded = catalog.Where(model => model.Id != target.Id && IsLoaded(model)).ToArray();
        var replace = false;
        if (loaded.Length > 0)
        {
            var count = loaded.Length;
            var selected = editor.ShowInlineSelectionList($"{count} model{(count == 1 ? " is" : "s are")} loaded",
                new[]
                {
                    new TerminalSelectionOption<string>("replace", "replace", "Unload all and load"),
                    new TerminalSelectionOption<string>("keep", "keep", "Keep loaded and load"),
                    new TerminalSelectionOption<string>("cancel", "cancel", "Cancel")
                }, [""], "enter select • escape cancel");
            if (selected is null || selected.Option.Value == "cancel") return;
            replace = selected.Option.Value == "replace";
        }

        if (replace)
            foreach (var model in loaded) await client.UnloadAndWaitAsync(model.Id, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await editor.RunProgressAsync("Loading model", target.Id, "Stop loading?", target.Id,
                (token, update) => client.LoadAndWaitAsync(target.Id,
                    progress => update(new(progress.Message, progress.Ratio, progress.Detail)), token),
                () => client.UnloadAsync(target.Id, CancellationToken.None)).ConfigureAwait(false);
            if (result.Cancelled)
            {
                if (replace) await RestoreLoadedAsync(client, loaded, cancellationToken).ConfigureAwait(false);
                return;
            }
            var loadedModel = (await runtime.RefreshLlamaRouterCatalogAsync(client, cancellationToken)
                .ConfigureAwait(false)).FirstOrDefault(model => model.Id == target.Id);
            Console.WriteLine(loadedModel?.Status.Value == "loaded" ? $"Loaded {target.Id}" : $"Load started for {target.Id}");
        }
        catch
        {
            if (replace)
            {
                try { await RestoreLoadedAsync(client, loaded, cancellationToken).ConfigureAwait(false); }
                catch { /* Keep the original load failure. */ }
            }
            throw;
        }
    }

    private async Task RestoreLoadedAsync(LlamaRouterClient client, IReadOnlyList<LlamaRouterModelInfo> models,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("Restoring previously loaded models");
        foreach (var model in models)
            await client.LoadAndWaitAsync(model.Id, _ => { }, cancellationToken).ConfigureAwait(false);
        _ = await runtime.RefreshLlamaRouterCatalogAsync(client, cancellationToken).ConfigureAwait(false);
    }

    private async Task UnloadModelAsync(LlamaRouterClient client, LlamaRouterModelInfo model,
        CancellationToken cancellationToken)
    {
        if (editor.ShowInlineSelectionList("Unload model?", new[]
            {
                new TerminalSelectionOption<bool>("yes", true, "Yes"),
                new TerminalSelectionOption<bool>("no", false, "No")
            }, [model.Id, ""], "enter select • escape cancel")?.Option.Value != true) return;
        await client.UnloadAndWaitAsync(model.Id, cancellationToken).ConfigureAwait(false);
        _ = await runtime.RefreshLlamaRouterCatalogAsync(client, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Unloaded {model.Id}");
    }

    private async Task DownloadModelAsync(LlamaRouterClient client, CancellationToken cancellationToken)
    {
        var entry = await editor.PromptSequenceAsync("Download model",
            [("Model name or owner/repository[:quant]", null)]).ConfigureAwait(false);
        if (entry is null || string.IsNullOrWhiteSpace(entry[0])) return;
        var input = entry[0].Trim();
        var configuredHuggingFaceEndpoint = environment("HF_ENDPOINT");
        var huggingFace = new HuggingFaceClient(http,
            await HuggingFaceClient.FindTokenAsync(environment, cancellationToken).ConfigureAwait(false),
            string.IsNullOrWhiteSpace(configuredHuggingFaceEndpoint) ? null : new Uri(configuredHuggingFaceEndpoint));
        string selected;
        if (s_repositoryInput.IsMatch(input)) selected = input;
        else
        {
            if (input.Length < 2) throw new ArgumentException("Enter at least two characters to search Hugging Face.");
            var search = await editor.RunProgressAsync("Searching Hugging Face", input, "Stop search?", input,
                async (token, update) =>
                {
                    update(new("Searching Hugging Face…"));
                    return await huggingFace.SearchAsync(input, token).ConfigureAwait(false);
                }, () => Task.CompletedTask).ConfigureAwait(false);
            if (search.Cancelled || search.Value is null) return;
            var results = search.Value;
            if (results.Count == 0) { Console.WriteLine("No GGUF models found"); return; }
            var options = results.Select(model => new TerminalSelectionOption<HuggingFaceModel>(model.Id, model,
                model.Id, CompactCount(model.Downloads) + " downloads", model.Id)).ToArray();
            selected = editor.ShowInlineSelectionList("Select Hugging Face model", options,
                [""], "enter select • escape back")?.Option.Value.Id ?? "";
            if (selected.Length == 0) return;
        }

        var (repository, requestedQuantization) = ParseRepositoryInput(selected);
        Console.WriteLine("Loading model details: " + repository);
        var details = await huggingFace.GetDetailsAsync(repository, cancellationToken).ConfigureAwait(false);
        if (details.Gated is { } gated)
        {
            var approval = gated == "manual" ? "Manual approval is required" : "Accept the access terms";
            var choice = editor.ShowInlineSelectionList("Hugging Face access required",
                new[]
                {
                    new TerminalSelectionOption<bool>("continue", true, "Continue"),
                    new TerminalSelectionOption<bool>("back", false, "Back")
                }, [details.Id, "", approval + " at:", $"https://huggingface.co/{details.Id}", "",
                    "The llama.cpp server needs HF_TOKEN with access."], "enter select • escape back");
            if (choice?.Option.Value != true) return;
        }

        var quantization = requestedQuantization;
        if (quantization is null && details.Quantizations.Count > 0)
        {
            var options = details.Quantizations.Select(item =>
            {
                var detail = new List<string>();
                if (item.Size is { } bytes) detail.Add(LlamaRouterClient.FormatBytes(bytes));
                if (item.Name == "Q4_K_M") detail.Add("recommended");
                return new TerminalSelectionOption<HuggingFaceQuantization>(item.Name, item,
                    item.Name + (detail.Count == 0 ? "" : " · " + string.Join(" · ", detail)));
            }).ToArray();
            quantization = editor.ShowInlineSelectionList("Select quantization", options,
                [details.Id], "enter select • escape back")?.Option.Value.Name;
            if (quantization is null) return;
        }

        var model = quantization is null ? details.Id : details.Id + ":" + quantization;
        var result = await editor.RunProgressAsync("Downloading model", model, "Stop download?", model,
            (token, update) => client.DownloadAndWaitAsync(model,
                progress => update(new(progress.Message, progress.Ratio, progress.Detail)), token),
            () => client.UnloadAsync(model, CancellationToken.None)).ConfigureAwait(false);
        if (result.Cancelled) return;
        _ = await runtime.RefreshLlamaRouterCatalogAsync(client, cancellationToken).ConfigureAwait(false);
        Console.WriteLine("Downloaded " + model);
    }

    private static (string Repository, string? Quantization) ParseRepositoryInput(string value)
    {
        var colon = value.IndexOf(':', value.IndexOf('/') + 1);
        return colon < 0 ? (value, null) : (value[..colon], value[(colon + 1)..]);
    }

    private static bool IsLoaded(LlamaRouterModelInfo model) => model.Status.Value is "loaded" or "sleeping";

    private static string ServerLabel(Uri serverUrl) => serverUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');

    private static string CompactCount(long value) => value >= 1_000_000
        ? (value / 1_000_000d).ToString(value >= 10_000_000 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + "M"
        : value >= 1_000
            ? (value / 1_000d).ToString(value >= 100_000 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + "k"
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed record LlamaAction(LlamaRouterModelInfo? Model, bool Download);
}
