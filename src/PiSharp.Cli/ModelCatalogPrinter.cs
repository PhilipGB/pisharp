using PiSharp.Core.Models;
using PiSharp.Core.Models.Providers;

namespace PiSharp.Cli;

/// <summary>
/// Renders the provider/model catalogue for --list-models (pinned listModels):
/// all registered providers and models, marking which are currently usable
/// (authenticated) via the runtime's available snapshot. Models that are
/// authenticated but on a wire API this build cannot execute are flagged
/// explicitly (selection boundary, item 3) so the list reflects what the
/// runtime can actually execute.
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

        var excludedExecutability = 0;
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
                var suffix = DescribeAvailability(model, available, ref excludedExecutability);
                var context = model.ContextWindow is { } window ? $" context {window / 1000}k" : string.Empty;
                var output = model.MaxTokens is { } max ? $" output {max / 1000}k" : string.Empty;
                var thinking = model.Reasoning ? " thinking" : string.Empty;
                Console.WriteLine($"  {model.Id}{context}{output}{thinking}{suffix}");
            }

            Console.WriteLine();
        }

        if (excludedExecutability > 0)
        {
            Console.WriteLine(
                $"Note: this build can only execute models on API '{string.Join("', '", ModelExecutionSupport.SupportedApis)}'. " +
                $"{excludedExecutability} authenticated model(s) are catalogued but not executable.");
        }
    }

    private static string DescribeAvailability(
        ModelInfo model,
        HashSet<string> available,
        ref int excludedExecutability)
    {
        if (!available.Contains(model.Reference))
        {
            return "  (not authenticated)";
        }

        if (!ModelExecutionSupport.CanExecute(model))
        {
            excludedExecutability++;
            return $"  [available - API '{model.Api}' not executable in this build]";
        }

        return "  [available]";
    }
}
