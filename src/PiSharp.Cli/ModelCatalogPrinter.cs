using PiSharp.Core.Models;
using PiSharp.Core.Models.Providers;

namespace PiSharp.Cli;

/// <summary>
/// Renders the provider/model catalogue for --list-models (pinned listModels):
/// all registered providers and models, marking which are currently usable
/// (authenticated) via the runtime's available snapshot.
/// </summary>
internal static class ModelCatalogPrinter
{
    /// <summary>Prints the model catalogue to stdout. Unknown providers are skipped.</summary>
    public static void Print(ModelRuntime runtime)
    {
        HashSet<string> available = [];
        try
        {
            foreach (var model in runtime.GetAvailableSnapshot())
            {
                available.Add(model.Reference);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Warning: could not determine authenticated models: {exception.Message}");
        }

        foreach (var provider in runtime.GetProviders())
        {
            var models = runtime.GetModels(provider.Id);
            if (models.Count == 0)
            {
                continue;
            }

            Console.WriteLine(provider.Id);
            foreach (var model in models)
            {
                var suffix = available.Contains(model.Reference) ? "  [available]" : "  (not authenticated)";
                var context = model.ContextWindow is { } window ? $" context {window / 1000}k" : string.Empty;
                var output = model.MaxTokens is { } max ? $" output {max / 1000}k" : string.Empty;
                var thinking = model.Reasoning ? " thinking" : string.Empty;
                Console.WriteLine($"  {model.Id}{context}{output}{thinking}{suffix}");
            }

            Console.WriteLine();
        }
    }
}
