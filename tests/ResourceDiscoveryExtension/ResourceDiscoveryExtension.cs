using PiSharp.Runtime.Extensions;
using Microsoft.Extensions.AI;
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

        registration.AddProjectTrustHandler(async (context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trace($"trust-decision:{context.HasUserInterface.ToString().ToLowerInvariant()}");
            var path = Path.Combine(context.WorkingDirectory, ".pi", "project-trust-extension.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var uiTrusted = false;
            if (root.TryGetProperty("exerciseUi", out var exerciseUi) && exerciseUi.ValueKind == JsonValueKind.True)
            {
                var selected = await context.Ui.SelectAsync("Trust option", ["Skip", "Continue"], cancellationToken);
                var confirmed = await context.Ui.ConfirmAsync("Continue?", "Trust project resources?", cancellationToken);
                var input = await context.Ui.InputAsync("Project label", "approved", cancellationToken);
                context.Ui.Notify("Project trust extension notification", "warning");
                Trace($"trust-ui:{context.Mode}:{context.HasUserInterface.ToString().ToLowerInvariant()}:{selected}:{confirmed.ToString().ToLowerInvariant()}:{input}");
                uiTrusted = selected == "Continue" && confirmed && input == "approved";
            }
            var decision = root.TryGetProperty("decision", out var decisionValue)
                ? decisionValue.GetString() : "undecided";
            if (decision == "throw") throw new InvalidOperationException("fixture trust hook failure");
            var parsed = decision switch
            {
                "yes" => ProjectTrustExtensionDecision.Yes,
                "no" => ProjectTrustExtensionDecision.No,
                "fromUi" => uiTrusted ? ProjectTrustExtensionDecision.Yes : ProjectTrustExtensionDecision.No,
                _ => ProjectTrustExtensionDecision.Undecided
            };
            var remember = root.TryGetProperty("remember", out var rememberValue) &&
                rememberValue.ValueKind == JsonValueKind.True;
            return new(parsed, remember);
        });

        if (string.Equals(Environment.GetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY"), "true",
                StringComparison.OrdinalIgnoreCase))
        {
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
    }

    private static void Trace(string value)
    {
        var path = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG");
        if (!string.IsNullOrWhiteSpace(path)) File.AppendAllText(path, value + "\n");
    }
}

public sealed class HiddenSkillReaderFixture : IPiSharpExtension
{
    public void Configure(ExtensionRegistration registration)
    {
        var hiddenDeclarations = Environment.GetEnvironmentVariable("PISHARP_SKILL_READER_HIDE")?
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (hiddenDeclarations is not { Length: > 0 }) return;

        registration.AddTool(new PiSharpToolRegistration(
            AIFunctionFactory.Create(() => "hidden-reader fixture", name: "hidden_skill_reader_fixture"),
            ToolExposure.ModelOnly,
            PrepareLoadout: _ => new ToolLoadoutChanges(HiddenDeclarations: hiddenDeclarations),
            PromptSnippet: "Fixture tool for hidden skill-reader prompt tests."));
    }
}
