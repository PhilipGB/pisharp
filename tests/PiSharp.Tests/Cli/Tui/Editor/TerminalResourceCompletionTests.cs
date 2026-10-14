using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Composition;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class TerminalResourceCompletionTests
{
    [Theory]
    [InlineData("<subject>", "<subject> — [t] Guide")]
    [InlineData(" ", "  — [t] Guide")]
    [InlineData("🌍", "🌍 — [t] Guide")]
    [InlineData(null, "[t] Guide")]
    public async Task PromptSuggestionShowsArgumentHintBeforeSourceDescriptionAndRefreshesOnReload(string? hint,
        string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-completion-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            var path = Path.Combine(root, "guide.md");
            await File.WriteAllTextAsync(path, "---\ndescription: Guide\n---\nReview $1");
            var arguments = CliArguments.Parse(["--local", "--no-extensions", "--no-context-files", "--no-session",
                "--no-skills", "--no-prompt-templates", "--prompt-template", path]);
            using var configuration = await ProjectRuntimeConfiguration.LoadAsync(root, agent, arguments,
                new ProjectTrust(agent), false, TextReader.Null, TextWriter.Null, trustedOverride: false);
            using var project = await ProjectRuntimeContext.LoadAsync(configuration, agent, arguments, null);
            var active = new ActiveProjectRuntime(configuration, project, arguments);
            using var extensions = new ExtensionLease(project.TransferExtensions());
            var resources = new TerminalResourceCompletion(active, extensions);
            var completion = new EditorCompletion(root, resources.Commands, resources.Describe);
            var buffer = new EditorBuffer();
            buffer.SetText("/gui");
            Assert.Equal("[t] Guide", Assert.Single(completion.GetSlashCommandSuggestions(buffer)).Description);

            await File.WriteAllTextAsync(path, $"---\ndescription: Guide\nargument-hint: {JsonSerializer.Serialize(hint)}\n---\nReview $1");
            using var reloaded = await ProjectRuntimeContext.LoadAsync(configuration, agent, arguments, null,
                resourceDiscoveryReason: ExtensionResourceDiscoveryReason.Reload);
            active.Adopt(configuration, reloaded);

            var suggestion = Assert.Single(completion.GetSlashCommandSuggestions(buffer));
            Assert.Equal("/guide", suggestion.Command);
            Assert.Equal(expected, suggestion.Description);
            Assert.Equal("/gui", buffer.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
