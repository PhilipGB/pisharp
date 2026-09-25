using System.Text;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalSelectionListTests
{
    [Fact]
    public void ProviderQualifiedSearchCanSwitchFromScopedToAllModels()
    {
        var all = new[]
        {
            new TerminalSelectionOption<string>("openai", "openai/gpt-4o", "gpt-4o [openai]",
                SearchText: "openai openai/gpt-4o gpt-4o"),
            new TerminalSelectionOption<string>("anthropic", "anthropic/claude-3.7", "claude-3.7 [anthropic]",
                SearchText: "anthropic anthropic/claude-3.7 claude-3.7")
        };
        var scoped = new[] { all[1] };
        var list = new TerminalSelectionList<string>("Select model", all, "anthropic", scoped);

        Assert.True(list.IsScoped);
        list.HandleInput(new(null, "openai/gpt"));
        Assert.Null(list.Selected);
        Assert.Equal(TerminalSelectionAction.Continue, list.HandleInput(Key(ConsoleKey.Tab, '\t')));
        Assert.False(list.IsScoped);
        Assert.Equal("openai/gpt-4o", list.Selected?.Option.Value);
        Assert.Equal(TerminalSelectionAction.Accept, list.HandleInput(Key(ConsoleKey.Enter, '\n')));
        Assert.Equal("openai/gpt-4o", list.Selected?.Option.Value);
    }

    [Fact]
    public void SearchUsesFuzzyTokensAndSelectionMovementWraps()
    {
        var options = new[]
        {
            new TerminalSelectionOption<string>("a", "anthropic/claude-3.7", "claude-3.7 [anthropic]",
                SearchText: "anthropic anthropic/claude-3.7 claude-3.7"),
            new TerminalSelectionOption<string>("o", "openai/gpt-4o", "gpt-4o [openai]",
                SearchText: "openai openai/gpt-4o gpt-4o")
        };
        var list = new TerminalSelectionList<string>("Select model", options);

        list.HandleInput(new(null, "anth/claude"));
        Assert.Equal("anthropic/claude-3.7", list.Selected?.Option.Value);
        list.HandleInput(Key(ConsoleKey.Backspace, '\b'));
        Assert.Equal("anthropic/claude-3.7", list.Selected?.Option.Value);
        list.HandleInput(Key(ConsoleKey.UpArrow));
        Assert.Equal("anthropic/claude-3.7", list.Selected?.Option.Value);
        Assert.Contains("Search: anth/claud", string.Join('\n', list.Render(50, 14)));
    }

    [Fact]
    public void EmptyScopedModelListCanSwitchToAllModels()
    {
        var all = new[] { new TerminalSelectionOption<string>("a", "model-a", "model-a") };
        var list = new TerminalSelectionList<string>("Select model", all, scopedOptions: []);

        Assert.True(list.IsScoped);
        Assert.Null(list.Selected);
        Assert.Contains("Scope: Scoped", string.Join('\n', list.Render(50, 14)));
        list.HandleInput(Key(ConsoleKey.Tab, '\t'));

        Assert.False(list.IsScoped);
        Assert.Equal("model-a", list.Selected?.Option.Value);
    }

    [Fact]
    public void EmptySessionListStaysOpenUntilExplicitCancellation()
    {
        var list = new TerminalSelectionList<string>("Resume session", [], emptyMessage: "No saved sessions");

        Assert.Null(list.Selected);
        Assert.Contains("No saved sessions", string.Join('\n', list.Render(50, 14)));
        Assert.Equal(TerminalSelectionAction.Continue, list.HandleInput(Key(ConsoleKey.Enter, '\n')));
        Assert.Equal(TerminalSelectionAction.Cancel, list.HandleInput(Key(ConsoleKey.Escape)));
    }

    [Fact]
    public void OverlayHostSelectsFromStreamAndSanitizesUntrustedLabels()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var inputStream = new MemoryStream(Encoding.UTF8.GetBytes("claude\r"));
        using var screen = new TerminalScreen(output, error, () => 48, () => 14);
        screen.SetEditor("draft stays intact", 18);
        var options = new[]
        {
            new TerminalSelectionOption<string>("openai", "openai/gpt-4o", "gpt-4o [openai]"),
            new TerminalSelectionOption<string>("anthropic", "anthropic/claude-3.7", "claude-3.7 [anthropic] \u001b[2Jevil")
        };
        var list = new TerminalSelectionList<string>("Select model", options);
        var filtered = new TerminalSelectionList<string>("Select model", options);
        filtered.HandleInput(new(null, "claude"));
        Assert.True(filtered.Selected!.Option.Label.All(character => character != '\u001b'),
            string.Join(",", filtered.Selected.Option.Label.Select(character => (int)character)));
        var rendered = list.Render(48, 14);
        Assert.False(rendered.Any(line => line.Contains('\u001b')),
            string.Join("|", rendered.Select(line => line.Replace("\u001b", "<ESC>"))));
        Assert.Contains("[2Jevil", string.Join('\n', rendered));
        Assert.Equal("gpt-4o [openai]", list.Selected!.Option.Label);
        var host = new TerminalOverlayHost(new TerminalInput(inputStream));

        var result = host.Select(screen, "Select model", options, "openai");

        Assert.Equal("anthropic/claude-3.7", result?.Option.Value);
        Assert.True(result!.Option.Label.All(character => character != '\u001b'),
            string.Join(",", result.Option.Label.Select(character => (int)character)));
        Assert.Contains("Select model", output.ToString());
        Assert.Contains("draft stays intact", output.ToString());
    }

    private static TerminalInputEvent Key(ConsoleKey key, char character = '\0', bool control = false) =>
        new(new ConsoleKeyInfo(character, key, false, false, control), null);
}
