using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
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

public sealed class PartialRegistrationFailureFixture : IPiSharpExtension
{
    public void Configure(ExtensionRegistration registration)
    {
        if (Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_PARTIAL_FAILURE") != "true" ||
            Path.GetFileName(GetType().Assembly.Location) != "bad.dll") return;
        registration.AddCommand("partial_command", (_, _) => Task.FromResult("Must not remain registered"),
            "Uncommitted extension command");
        registration.AddLifecycleHandler(_ => { });
        registration.AddTool(AIFunctionFactory.Create(() => "Must not remain registered", name: "partial_tool"));
        using var mcp = JsonDocument.Parse("{\"command\":\"false\"}");
        registration.RegisterMcpServer("partial_mcp", mcp.RootElement);
        registration.RegisterVirtualModel(new("partial", "model", "Uncommitted virtual model",
            (_, _) => Task.FromResult(new PiSharp.Runtime.VirtualModels.VirtualModelRoute("fixture", "model", "off"))));
        throw new InvalidOperationException("fixture extension configuration failure");
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
            new HiddenSkillReaderFunction(AIFunctionFactory.Create(() => "hidden-reader fixture",
                name: "hidden_skill_reader_fixture", description: "Fixture tool for hidden skill-reader prompt tests.")),
            ToolExposure.ModelOnly,
            PrepareLoadout: _ => new ToolLoadoutChanges(HiddenDeclarations: hiddenDeclarations)));
    }

    private sealed class HiddenSkillReaderFunction(AIFunction inner) : DelegatingAIFunction(inner)
    {
        private static readonly JsonElement s_schema = CreateSchema();

        public override JsonElement JsonSchema => s_schema;

        private static JsonElement CreateSchema()
        {
            using var document = JsonDocument.Parse("""
                {"type":"object","properties":{},"additionalProperties":false}
                """);
            return document.RootElement.Clone();
        }
    }
}

public sealed class ExtensionConflictFixture : IPiSharpExtension
{
    public void Configure(ExtensionRegistration registration)
    {
        var name = Path.GetFileName(typeof(ExtensionConflictFixture).Assembly.Location);
        if (name is not ("project.dll" or "user.dll")) return;

        var owner = Path.GetFileNameWithoutExtension(name);
        registration.AddTool(AIFunctionFactory.Create(() => owner, name: "shared_tool",
            description: owner + " tool"));
        registration.AddCommand("deploy", (_, _) => Task.FromResult(owner), owner + " command");
        if (owner == "project")
            registration.AddCommand("mcp", (_, _) => Task.FromResult(owner), "project mcp command");
        else
            registration.AddCommand("user_only", (_, _) => Task.FromResult(owner), "user command");
    }
}

public sealed class ExtensionContextFixture : IPiSharpExtension
{
    public void Configure(ExtensionRegistration registration)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_CONTEXT"), "true",
                StringComparison.OrdinalIgnoreCase)) return;

        registration.AddResourceDiscoveryHandler((context, _) =>
        {
            Trace("resource:" + (context.Reason == ExtensionResourceDiscoveryReason.Reload ? "reload" : "startup"));
            return Task.FromResult<ExtensionResourceDiscoveryResult?>(null);
        });
        registration.AddLifecycleHandler((item, context) =>
        {
            if (item.Tool == "extension_context_probe" && item.Type is "tool_execution_started" or
                "tool_execution_update" or "tool_execution_finished")
                Trace("event:" + item.Type + ":" + context.CurrentModelId);
        });
        registration.AddTool(AIFunctionFactory.Create((AIFunctionArguments arguments) =>
        {
            var invocation = PiSharpToolExecutionContext.Get(arguments) ??
                throw new InvalidOperationException("PiSharp tool invocation context was not attached.");
            var context = invocation.ExtensionContext ??
                throw new InvalidOperationException("PiSharp extension context was not attached.");
            invocation.ReportProgress("extension context progress");
            context.Ui.Notify("extension context notification", "warning");
            var state = new
            {
                context.WorkingDirectory,
                context.Mode,
                context.HasUserInterface,
                context.IsProjectTrusted,
                context.IsIdle,
                context.CurrentModelId,
                context.CurrentProviderId,
                context.SessionId,
                context.SessionName,
                context.ConversationHeadId,
                ActiveToolNames = context.ActiveToolNames
            };
            Trace("state:" + JsonSerializer.Serialize(state, JsonSerializerOptions.Web));
            return JsonSerializer.Serialize(state, JsonSerializerOptions.Web);
        }, name: "extension_context_probe", description: "Report live native extension context for parity tests."));
    }

    private static void Trace(string value)
    {
        var path = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_CONTEXT_TRACE");
        if (!string.IsNullOrWhiteSpace(path)) File.AppendAllText(path, value + "\n");
    }
}
