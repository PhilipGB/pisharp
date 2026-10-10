using PiSharp.Runtime.Extensions;
using System.Text.Json;

namespace PiSharp.ResourceDiscoveryExtension;

public sealed class ResourceDiscoveryFixture : IPiSharpExtension
{
    public void Configure(ExtensionRegistration registration)
    {
        Trace("configured");

        registration.AddProjectTrustHandler((context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trace($"trust-undecided:{context.HasUserInterface.ToString().ToLowerInvariant()}");
            return Task.FromResult<ProjectTrustExtensionResult?>(new(
                ProjectTrustExtensionDecision.Undecided, Remember: true));
        });

        registration.AddProjectTrustHandler((context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trace($"trust-decision:{context.HasUserInterface.ToString().ToLowerInvariant()}");
            var path = Path.Combine(context.WorkingDirectory, ".pi", "project-trust-extension.json");
            if (!File.Exists(path)) return Task.FromResult<ProjectTrustExtensionResult?>(null);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var decision = root.TryGetProperty("decision", out var decisionValue)
                ? decisionValue.GetString() : "undecided";
            if (decision == "throw") throw new InvalidOperationException("fixture trust hook failure");
            var parsed = decision switch
            {
                "yes" => ProjectTrustExtensionDecision.Yes,
                "no" => ProjectTrustExtensionDecision.No,
                _ => ProjectTrustExtensionDecision.Undecided
            };
            var remember = root.TryGetProperty("remember", out var rememberValue) &&
                rememberValue.ValueKind == JsonValueKind.True;
            return Task.FromResult<ProjectTrustExtensionResult?>(new(parsed, remember));
        });

        registration.AddResourceDiscoveryHandler((context, _) =>
        {
            var phase = context.Reason == ExtensionResourceDiscoveryReason.Reload ? "reloaded" : "startup";
            var root = Path.Combine(context.WorkingDirectory, ".pi", "extension-resources", phase);
            return Task.FromResult<ExtensionResourceDiscoveryResult?>(new(
                [new Uri(Path.Combine(root, "skills")).AbsoluteUri],
                [Path.Combine(root, "prompts")],
                [Path.Combine(root, "themes")]));
        });
    }

    private static void Trace(string value)
    {
        var path = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG");
        if (!string.IsNullOrWhiteSpace(path)) File.AppendAllText(path, value + "\n");
    }
}
