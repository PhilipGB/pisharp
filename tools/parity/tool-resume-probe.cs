using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Composition;
using PiSharp.Cli.Sessions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

var results = new List<object>();
foreach (var id in JsonSerializer.Deserialize<string[]>(args[0])!)
{
    var root = Path.Combine(Path.GetTempPath(), "pisharp-tool-resume-" + Guid.NewGuid().ToString("N"));
    var agentDirectory = Path.Combine(root, "agent");
    Directory.CreateDirectory(agentDirectory);
    try
    {
        string[] flags = id switch
        {
            "explicit" => ["--tools", "read,write"],
            "modifiers" => ["--tools", "-grep,+write"],
            "excluded" => ["--exclude-tools", "inactive_tool"],
            "disabled" => ["--no-tools"],
            "fresh-modifiers" => ["--tools", "-write,-active_tool"],
            _ => []
        };
        var arguments = DefaultToolReloadPolicy.ApplyStartupDefaults(
            CliArguments.Parse(["--local", "--no-extensions", "--no-session", .. flags]),
            id == "settings" ? new UserSettings(DefaultTools: ["read"]) : new UserSettings(), _ => null, false);
        using var configuration = await ProjectRuntimeConfiguration.LoadAsync(root, agentDirectory, arguments,
            new ProjectTrust(agentDirectory), false, TextReader.Null, TextWriter.Null, trustedOverride: true);
        using var project = await ProjectRuntimeContext.LoadAsync(configuration, agentDirectory, arguments, null);
        project.Extensions.Registration.AddTool(new PiSharpToolRegistration(
            AIFunctionFactory.Create(() => "inactive", name: "inactive_tool"), DefaultActive: false));
        project.Extensions.Registration.AddTool(new PiSharpToolRegistration(
            AIFunctionFactory.Create(() => "active", name: "active_tool")));
        using var http = new HttpClient();
        var providers = await ProviderModelRuntime.CreateAsync(agentDirectory, true, _ => null, http, offline: true);
        var selection = await providers.ResolveAsync("local", null);
        var activeProject = new ActiveProjectRuntime(configuration, project, arguments);
        var controller = new ModelRuntimeController(providers, () => activeProject.Settings, _ => null);
        using var chat = ProviderChatClientFactory.Create(selection);
        var agent = project.CreateAgent(chat, selection, "off", arguments);
        var model = new ActiveModelRuntime(selection, chat, agent, "off", null, null);
        var session = new ConversationSession(root, selection.Connection.Model, null, "local");
        if (!id.StartsWith("fresh", StringComparison.Ordinal))
        {
            var message = new ChatMessage(ChatRole.System, "instructions");
            SystemMessageTranscript.ImportMetadata(message, JsonSerializer.SerializeToElement(new
            {
                toolsAdded = (id == "empty" ? [] : new[] { "read", "grep", "inactive_tool" }).Select(name => new { name })
            }));
            session.Append(message);
            if (id == "update")
            {
                var update = new ChatMessage(ChatRole.System, "updated");
                SystemMessageTranscript.ImportMetadata(update, JsonSerializer.SerializeToElement(new
                {
                    toolsAdded = new[] { new { name = "write" } }, toolsRemoved = new[] { new { name = "grep" } }
                }));
                session.Append(update);
            }
        }
        var run = await new ConversationRunComposition(activeProject, controller).OpenAsync(model, agent, session, null);
        results.Add(new { id, active = run.ActiveToolNames.Order(StringComparer.Ordinal).ToArray() });
    }
    finally { Directory.Delete(root, true); }
}
Console.WriteLine(JsonSerializer.Serialize(results));
