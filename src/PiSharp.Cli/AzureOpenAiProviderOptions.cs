using System.Collections.ObjectModel;

namespace PiSharp.Cli;

/// <summary>Provider-specific request settings that Pi reads from Azure environment variables.</summary>
public sealed record AzureOpenAiProviderOptions(string ApiVersion,
    IReadOnlyDictionary<string, string> DeploymentNames)
{
    public static AzureOpenAiProviderOptions FromEnvironment(Func<string, string?> environment)
    {
        var apiVersion = environment("AZURE_OPENAI_API_VERSION");
        var deploymentNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var mappings = environment("AZURE_OPENAI_DEPLOYMENT_NAME_MAP");
        if (!string.IsNullOrEmpty(mappings))
        {
            foreach (var entry in mappings.Split(','))
            {
                var separator = entry.IndexOf('=');
                if (separator <= 0 || separator == entry.Length - 1) continue;
                var modelId = entry[..separator].Trim();
                var deploymentName = entry[(separator + 1)..].Trim();
                if (modelId.Length > 0 && deploymentName.Length > 0)
                    deploymentNames[modelId] = deploymentName;
            }
        }
        return new(string.IsNullOrWhiteSpace(apiVersion) ? "v1" : apiVersion.Trim(),
            new ReadOnlyDictionary<string, string>(deploymentNames));
    }
}
