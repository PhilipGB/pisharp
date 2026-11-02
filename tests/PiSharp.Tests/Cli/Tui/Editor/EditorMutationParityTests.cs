using System.Globalization;
using System.Text.Json;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class EditorMutationParityTests
{
    private sealed record Cursor(int Line, int Column);
    private sealed record Step(string Text, Cursor Cursor);
    private sealed record Scenario(string Text, int? Offset, string[] Steps, Step[] Expected);

    private static readonly IReadOnlyDictionary<string, Scenario> s_scenarios = LoadScenarios();

    public static TheoryData<string> ScenarioNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in s_scenarios.Keys) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void MatchesPinnedPi(string name)
    {
        var scenario = s_scenarios[name];
        var editor = new EditorBuffer();
        editor.SetText(scenario.Text);
        if (scenario.Offset is { } offset) Position(editor, offset);
        for (var index = 0; index < scenario.Steps.Length; index++)
        {
            Apply(editor, scenario.Steps[index]);
            Assert.Equal(scenario.Expected[index].Text, editor.Text);
            Assert.Equal(scenario.Expected[index].Cursor, CursorOf(editor));
        }
    }

    [Fact]
    public void SelectionDeletionReplacementAndKillsKeepCursorAndUndoIntegrity()
    {
        var editor = new EditorBuffer();
        editor.SetText("alpha beta gamma");
        editor.SetCursor(11);
        SelectLeft(editor, 5);

        Assert.Equal("beta ", editor.SelectedText);
        Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.Backspace)));
        Assert.Equal("alpha gamma", editor.Text);
        Assert.Equal(6, editor.Cursor);
        Assert.Null(editor.SelectedText);
        Assert.Equal(EditorAction.Render, editor.Handle(Undo));
        Assert.Equal("alpha beta gamma", editor.Text);
        Assert.Equal(6, editor.Cursor);
        Assert.Equal("beta ", editor.SelectedText);

        editor.SetCursor(11);
        SelectLeft(editor, 5);
        Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.X, keyChar: 'X')));
        Assert.Equal("alpha Xgamma", editor.Text);
        Assert.Equal(7, editor.Cursor);
        Assert.Null(editor.SelectedText);
        Assert.Equal(EditorAction.Render, editor.Handle(Undo));
        Assert.Equal("alpha beta gamma", editor.Text);
        Assert.Equal("beta ", editor.SelectedText);

        editor.SetCursor(11);
        SelectLeft(editor, 5);
        Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.U, control: true)));
        Assert.Equal("alpha gamma", editor.Text);
        Assert.Equal(6, editor.Cursor);
        Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.Y, control: true)));
        Assert.Equal("alpha beta gamma", editor.Text);
        Assert.Equal(11, editor.Cursor);
        Assert.Null(editor.SelectedText);

        editor.SetCursor(11);
        SelectLeft(editor, 5);
        Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.K, control: true)));
        Assert.Equal("alpha gamma", editor.Text);
        Assert.Equal(6, editor.Cursor);
        Assert.Equal(EditorAction.Render, editor.Handle(Undo));
        Assert.Equal("alpha beta gamma", editor.Text);
        Assert.Equal("beta ", editor.SelectedText);

        editor.SetCursor(11);
        SelectLeft(editor, 5);
        Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.D, keyChar: 'd', alt: true)));
        Assert.Equal("alpha gamma", editor.Text);
        Assert.Equal(6, editor.Cursor);
        Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.Y, control: true)));
        Assert.Equal("alpha beta gamma", editor.Text);
        Assert.Equal(11, editor.Cursor);
    }

    [Fact]
    public void SelectionAcrossGraphemesAndLinesUsesLinearOffsets()
    {
        var editor = new EditorBuffer();
        editor.SetText("a😀e\u0301\nb");
        editor.SetCursor(5);
        SelectLeft(editor, 3);

        Assert.Equal("a😀e\u0301", editor.SelectedText);
        Assert.Equal(0, editor.Cursor);
        Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.Backspace)));
        Assert.Equal("\nb", editor.Text);
        Assert.Equal(0, editor.Cursor);

        editor.SetText("a😀e\u0301\nb");
        editor.SetCursor(6);
        SelectLeft(editor, 2);
        Assert.Equal("e\u0301\n", editor.SelectedText);
        Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.Delete)));
        Assert.Equal("a😀b", editor.Text);
        Assert.Equal(3, editor.Cursor);
    }

    [Fact]
    public void YankReplacesSelectionAndUndoRestoresIt()
    {
        var editor = new EditorBuffer();
        editor.SetText("keep");
        editor.SetCursor(4);
        Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.W, control: true)));
        Assert.Equal("", editor.Text);

        editor.SetText("one two");
        editor.SetCursor(3);
        SelectLeft(editor, 3);
        Assert.Equal("one", editor.SelectedText);
        Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.Y, control: true)));
        Assert.Equal("keep two", editor.Text);
        Assert.Equal(4, editor.Cursor);
        Assert.Null(editor.SelectedText);

        Assert.Equal(EditorAction.Render, editor.Handle(Undo));
        Assert.Equal("one two", editor.Text);
        Assert.Equal("one", editor.SelectedText);
    }

    [Fact]
    public void CtrlDExitsOnlyWhenTheEditorIsEmpty()
    {
        var editor = new EditorBuffer();
        Assert.Equal(EditorAction.Exit, editor.Handle(Key(ConsoleKey.D, control: true)));

        editor.SetText("   ");
        Assert.NotEqual(EditorAction.Exit, editor.Handle(Key(ConsoleKey.D, control: true)));
        Assert.Equal("   ", editor.Text);

        editor.SetText("abc");
        editor.SetCursor(0);
        Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.D, control: true)));
        Assert.Equal("bc", editor.Text);
        Assert.NotEqual(EditorAction.Exit, editor.Handle(Key(ConsoleKey.D, control: true)));
        Assert.Equal("c", editor.Text);
    }

    [Fact]
    public void ConfiguredMutationBindingsReplaceDefaultsAndReloadAppliesChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-mutation-bindings-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "keybindings.json");
            File.WriteAllText(path, """
                {
                  "tui.editor.deleteWordBackward": "ctrl+x",
                  "tui.editor.deleteWordForward": "ctrl+q",
                  "tui.editor.deleteToLineStart": "ctrl+n",
                  "tui.editor.deleteToLineEnd": "f7",
                  "tui.editor.yank": "f8",
                  "tui.editor.yankPop": "f9",
                  "tui.editor.undo": "ctrl+z"
                }
                """);
            var keymap = new EditorKeymap(directory);
            var editor = new EditorBuffer(keymap);
            editor.SetText("one two three");

            Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.X, control: true)));
            Assert.Equal("one two ", editor.Text);
            Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.Z, control: true)));
            Assert.Equal("one two three", editor.Text);

            File.WriteAllText(path, """
                {
                  "tui.editor.deleteWordBackward": "ctrl+p",
                  "tui.editor.deleteWordForward": "ctrl+q",
                  "tui.editor.deleteToLineStart": "ctrl+n",
                  "tui.editor.deleteToLineEnd": "f7",
                  "tui.editor.yank": "f8",
                  "tui.editor.yankPop": "f9",
                  "tui.editor.undo": "ctrl+u"
                }
                """);
            keymap.Reload();
            Assert.Equal(EditorAction.None, editor.Handle(Key(ConsoleKey.X, control: true)));
            Assert.Equal("one two three", editor.Text);
            Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.P, control: true)));
            Assert.Equal("one two ", editor.Text);
            Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.U, control: true)));
            Assert.Equal("one two three", editor.Text);

            editor.SetText("one two three");
            editor.SetCursor(0);
            Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.Q, control: true)));
            Assert.Equal(" two three", editor.Text);

            editor.SetText("abc def");
            editor.SetCursor(4);
            Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.N, control: true)));
            Assert.Equal("def", editor.Text);
            Assert.Equal(0, editor.Cursor);

            editor.SetText("abc def");
            editor.SetCursor(4);
            Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.F7)));
            Assert.Equal("abc ", editor.Text);
            Assert.Equal(4, editor.Cursor);

            editor.SetText("");
            Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.F8)));
            Assert.Equal("def", editor.Text);
            Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.F9)));
            Assert.Equal("abc ", editor.Text);
            Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.U, control: true)));
            Assert.Equal("def", editor.Text);
            Assert.Equal(EditorAction.Render, editor.Handle(Key(ConsoleKey.U, control: true)));
            Assert.Equal("", editor.Text);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Apply(EditorBuffer editor, string step)
    {
        if (step.StartsWith("type:", StringComparison.Ordinal))
        {
            foreach (var character in step[5..]) editor.Handle(Key(ConsoleKey.NoName, character));
            return;
        }
        if (step.StartsWith("pos:", StringComparison.Ordinal))
        {
            Position(editor, int.Parse(step[4..], CultureInfo.InvariantCulture));
            return;
        }
        if (step.StartsWith("set:", StringComparison.Ordinal))
        {
            editor.SetText(step[4..]);
            return;
        }
        editor.Handle(step switch
        {
            "bs" => Key(ConsoleKey.Backspace),
            "del" => Key(ConsoleKey.Delete),
            "alt-bs" => Key(ConsoleKey.Backspace, alt: true),
            "alt-del" => Key(ConsoleKey.Delete, alt: true),
            "ctrl-d" => Key(ConsoleKey.D, control: true),
            "ctrl-w" => Key(ConsoleKey.W, control: true),
            "alt-d" => Key(ConsoleKey.D, keyChar: 'd', alt: true),
            "ctrl-u" => Key(ConsoleKey.U, control: true),
            "ctrl-k" => Key(ConsoleKey.K, control: true),
            "ctrl-y" => Key(ConsoleKey.Y, control: true),
            "alt-y" => Key(ConsoleKey.Y, keyChar: 'y', alt: true),
            "ctrl-minus" => Key(ConsoleKey.OemMinus, keyChar: '-', control: true),
            "ctrl-a" => Key(ConsoleKey.A, control: true),
            "ctrl-e" => Key(ConsoleKey.E, control: true),
            "left" => Key(ConsoleKey.LeftArrow),
            "right" => Key(ConsoleKey.RightArrow),
            "up" => Key(ConsoleKey.UpArrow),
            "down" => Key(ConsoleKey.DownArrow),
            "newline" => Key(ConsoleKey.Enter, keyChar: '\n', alt: true),
            _ => throw new ArgumentException($"Unknown step: {step}")
        });
    }

    private static void Position(EditorBuffer editor, int offset)
    {
        var before = editor.Text[..offset];
        var targetLine = before.Count(character => character == '\n');
        var targetColumn = offset - (before.LastIndexOf('\n') + 1);
        editor.Handle(Key(ConsoleKey.A, control: true));
        while (CursorOf(editor).Line > targetLine) editor.Handle(Key(ConsoleKey.UpArrow));
        while (CursorOf(editor).Line < targetLine) editor.Handle(Key(ConsoleKey.DownArrow));
        editor.Handle(Key(ConsoleKey.A, control: true));
        for (var index = 0; index < targetColumn; index++) editor.Handle(Key(ConsoleKey.RightArrow));
    }

    private static Cursor CursorOf(EditorBuffer editor)
    {
        var before = editor.Text[..editor.Cursor];
        return new(before.Count(character => character == '\n'),
            editor.Cursor - (before.LastIndexOf('\n') + 1));
    }

    private static void SelectLeft(EditorBuffer editor, int graphemes)
    {
        for (var index = 0; index < graphemes; index++)
            editor.Handle(Key(ConsoleKey.LeftArrow, shift: true));
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char keyChar = '\0', bool shift = false, bool alt = false,
        bool control = false) => new(keyChar, key, shift, alt, control);

    private static ConsoleKeyInfo Undo => Key(ConsoleKey.OemMinus, keyChar: '-', control: true);

    private static IReadOnlyDictionary<string, Scenario> LoadScenarios()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("PiSharp repository root was not found.");
        var path = Path.Combine(root, "docs", "parity", "fixtures",
            "keys-editor-mutation-pi-oracle-2026-11-02.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, Scenario>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.GetProperty("scenarios").EnumerateObject())
        {
            var value = property.Value;
            result[property.Name] = new Scenario(
                value.GetProperty("text").GetString()!,
                value.TryGetProperty("offset", out var offset) ? offset.GetInt32() : null,
                value.GetProperty("steps").EnumerateArray().Select(step => step.GetString()!).ToArray(),
                value.GetProperty("expected").EnumerateArray().Select(step => new Step(
                    step.GetProperty("text").GetString()!,
                    new Cursor(step.GetProperty("cursor").GetProperty("line").GetInt32(),
                        step.GetProperty("cursor").GetProperty("col").GetInt32()))).ToArray());
        }
        return result;
    }
}
