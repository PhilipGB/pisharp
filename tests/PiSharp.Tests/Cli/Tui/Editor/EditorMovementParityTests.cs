using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class EditorMovementParityTests
{
    private sealed record Cursor(int Line, int Col);
    private sealed record Visual(int Row, int Column);
    private sealed record ViewportCursor(int Row, int Column);
    private sealed record Step(string Text, Cursor Cursor, Visual Visual, ViewportCursor ViewportCursor,
        string[] Viewport, string? Selection);
    private sealed record Scenario(string Text, Cursor? Cursor, int Width, int Rows, string[] Steps,
        string[] History, IReadOnlyDictionary<string, string[]> Bindings, Step[] Expected);

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
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-movement-bindings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (scenario.Bindings.Count > 0)
                File.WriteAllText(Path.Combine(directory, "keybindings.json"),
                    JsonSerializer.Serialize(scenario.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value)));
            var editor = new EditorBuffer(new EditorKeymap(directory));
            editor.SetLayoutWidth(scenario.Width);
            editor.SetTerminalRows(scenario.Rows);
            editor.SetText(scenario.Text);
            if (scenario.Cursor is { } cursor) editor.SetCursor(OffsetOf(editor.Text, cursor));
            foreach (var entry in scenario.History) editor.AddHistoryEntry(entry);

            var scrollOffset = 0;
            var currentWidth = scenario.Width;
            var currentRows = scenario.Rows;
            for (var index = 0; index < scenario.Steps.Length; index++)
            {
                Apply(editor, scenario.Steps[index]);
                if (scenario.Steps[index].StartsWith("resize:", StringComparison.Ordinal))
                    currentWidth = int.Parse(scenario.Steps[index][7..], CultureInfo.InvariantCulture);
                if (scenario.Steps[index].StartsWith("rows:", StringComparison.Ordinal))
                    currentRows = int.Parse(scenario.Steps[index][5..], CultureInfo.InvariantCulture);
                var expected = scenario.Expected[index];
                Assert.Equal(expected.Text, editor.Text);
                Assert.True(expected.Cursor == CursorOf(editor),
                    $"{name} step {index} ({scenario.Steps[index]}): cursor {CursorOf(editor)} expected {expected.Cursor}");
                Assert.Equal(expected.Visual, VisualOf(editor));
                var frame = EditorViewport.Layout(editor.Text, editor.Cursor, currentWidth + 1,
                    MaxVisibleLines(currentRows), showPrompt: false, scrollOffset: scrollOffset);
                scrollOffset = frame.ScrollOffset;
                Assert.True(expected.ViewportCursor == new ViewportCursor(frame.CursorRow, frame.CursorColumn),
                    $"{name} step {index} ({scenario.Steps[index]}): viewport cursor " +
                    $"{(frame.CursorRow, frame.CursorColumn)} expected {expected.ViewportCursor}");
                var actualViewport = frame.ContentRows.Select(row => row.TrimEnd()).ToArray();
                Assert.True(expected.Viewport.SequenceEqual(actualViewport),
                    $"{name} step {index} ({scenario.Steps[index]}): viewport " +
                    $"[{string.Join(" | ", actualViewport)}] expected [{string.Join(" | ", expected.Viewport)}]");
                Assert.Equal(expected.Selection, editor.SelectedText);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }


    [Fact]
    public void BufferedJumpTextConsumesOnlyTheNextGrapheme()
    {
        var editor = new EditorBuffer();
        editor.SetText("a,b,c");
        editor.SetCursor(0);
        editor.Handle(Key(ConsoleKey.Oem6, control: true));
        editor.HandleJumpText(",XY");
        Assert.Equal("aXY,b,c", editor.Text);
        Assert.Equal(3, editor.Cursor);
        Assert.False(editor.IsJumpModeActive);
        editor.Handle(Key(ConsoleKey.NoName, 'Z'));
        Assert.Equal("aXYZ,b,c", editor.Text);
    }

    [Fact]
    public void LegacyCtrlBracketEntersJumpMode()
    {
        var reader = new PiSharp.Cli.Tui.TerminalInput(new MemoryStream("\u001d,"u8.ToArray()));
        var jump = reader.Read().Key!.Value;
        Assert.True(new EditorKeymap().Matches("tui.editor.jumpForward", jump));
        var editor = new EditorBuffer();
        editor.SetText("alpha");
        editor.SetCursor(0);
        editor.Handle(jump);
        editor.Handle(Key(ConsoleKey.NoName, ','));
        Assert.Equal("alpha", editor.Text);
        Assert.Equal(0, editor.Cursor);
    }


    [Fact]
    public void KittyCtrlBracketEntersJumpMode()
    {
        var reader = new PiSharp.Cli.Tui.TerminalInput(new MemoryStream("\u001b[93;5u,"u8.ToArray()));
        var jump = reader.Read().Key!.Value;
        Assert.True(new EditorKeymap().Matches("tui.editor.jumpForward", jump));
    }

    private static void Apply(EditorBuffer editor, string step)
    {
        if (step.StartsWith("type:", StringComparison.Ordinal))
        {
            editor.InsertText(step[5..]);
            return;
        }
        if (step.StartsWith("char:", StringComparison.Ordinal))
        {
            editor.Handle(Key(ConsoleKey.NoName, step[5]));
            return;
        }
        if (step.StartsWith("set:", StringComparison.Ordinal))
        {
            editor.SetText(step[4..]);
            return;
        }
        if (step.StartsWith("resize:", StringComparison.Ordinal))
        {
            editor.SetLayoutWidth(int.Parse(step[7..], CultureInfo.InvariantCulture));
            return;
        }
        if (step.StartsWith("rows:", StringComparison.Ordinal))
        {
            editor.SetTerminalRows(int.Parse(step[5..], CultureInfo.InvariantCulture));
            return;
        }
        if (step == "submit")
        {
            editor.TrySubmit(out _);
            editor.Clear();
            return;
        }
        editor.Handle(step switch
        {
            "left" => Key(ConsoleKey.LeftArrow),
            "right" => Key(ConsoleKey.RightArrow),
            "up" => Key(ConsoleKey.UpArrow),
            "down" => Key(ConsoleKey.DownArrow),
            "ctrl-b" => Key(ConsoleKey.B, control: true),
            "ctrl-f" => Key(ConsoleKey.F, control: true),
            "alt-left" => Key(ConsoleKey.LeftArrow, alt: true),
            "ctrl-left" => Key(ConsoleKey.LeftArrow, control: true),
            "alt-b" => Key(ConsoleKey.B, keyChar: 'b', alt: true),
            "alt-right" => Key(ConsoleKey.RightArrow, alt: true),
            "ctrl-right" => Key(ConsoleKey.RightArrow, control: true),
            "alt-f" => Key(ConsoleKey.F, keyChar: 'f', alt: true),
            "home" => Key(ConsoleKey.Home),
            "end" => Key(ConsoleKey.End),
            "ctrl-a" => Key(ConsoleKey.A, control: true),
            "ctrl-e" => Key(ConsoleKey.E, control: true),
            "ctrl-home" => Key(ConsoleKey.Home, control: true),
            "ctrl-end" => Key(ConsoleKey.End, control: true),
            "pageup" => Key(ConsoleKey.PageUp),
            "pagedown" => Key(ConsoleKey.PageDown),
            "ctrl-pageup" => Key(ConsoleKey.PageUp, control: true),
            "ctrl-pagedown" => Key(ConsoleKey.PageDown, control: true),
            "jump-forward" => Key(ConsoleKey.Oem6, control: true),
            "jump-backward" => Key(ConsoleKey.Oem6, control: true, alt: true),
            "ctrl-p" => Key(ConsoleKey.P, control: true),
            "ctrl-n" => Key(ConsoleKey.N, control: true),
            "ctrl-r" => Key(ConsoleKey.R, control: true),
            "ctrl-l" => Key(ConsoleKey.L, control: true),
            "f7" => Key(ConsoleKey.F7),
            "f8" => Key(ConsoleKey.F8),
            "f9" => Key(ConsoleKey.F9),
            "f10" => Key(ConsoleKey.F10),
            "f11" => Key(ConsoleKey.F11),
            "f12" => Key(ConsoleKey.F12),
            "alt-j" => Key(ConsoleKey.J, keyChar: 'j', alt: true),
            "alt-k" => Key(ConsoleKey.K, keyChar: 'k', alt: true),
            _ => throw new ArgumentException($"Unknown step: {step}")
        });
    }

    private static Cursor CursorOf(EditorBuffer editor)
    {
        var before = editor.Text[..editor.Cursor];
        return new(before.Count(character => character == '\n'),
            editor.Cursor - (before.LastIndexOf('\n') + 1));
    }

    private static Visual VisualOf(EditorBuffer editor)
    {
        var lines = EditorVisualLines.Build(editor.Text, editor.LayoutWidth);
        var cursor = CursorOf(editor);
        var index = EditorVisualLines.FindVisualLine(lines, cursor.Line, editor.Cursor, lines.Count - 1);
        var line = lines[index];
        return new(index, VisibleWidth(editor.Text[line.StartOffset..editor.Cursor]));
    }

    private static int VisibleWidth(string text)
    {
        var width = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) width += TerminalCells.Width((string)enumerator.Current);
        return width;
    }

    private static int MaxVisibleLines(int rows) => Math.Max(5, (int)Math.Floor(rows * 0.3));

    private static int OffsetOf(string text, Cursor cursor)
    {
        var lines = text.Split('\n');
        var offset = 0;
        for (var index = 0; index < cursor.Line; index++) offset += lines[index].Length + 1;
        return offset + cursor.Col;
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char keyChar = '\0', bool shift = false, bool alt = false,
        bool control = false) => new(keyChar, key, shift, alt, control);

    private static IReadOnlyDictionary<string, Scenario> LoadScenarios()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("PiSharp repository root was not found.");
        var path = Path.Combine(root, "docs", "parity", "fixtures",
            "keys-editor-movement-pi-oracle-2026-11-02.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, Scenario>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.GetProperty("scenarios").EnumerateObject())
        {
            var value = property.Value;
            var cursor = value.TryGetProperty("cursor", out var cursorElement)
                ? new Cursor(cursorElement.GetProperty("line").GetInt32(), cursorElement.GetProperty("col").GetInt32())
                : null;
            var bindings = value.TryGetProperty("bindings", out var bindingsElement)
                ? bindingsElement.EnumerateObject().ToDictionary(pair => pair.Name,
                    pair => pair.Value.ValueKind == JsonValueKind.Array
                        ? pair.Value.EnumerateArray().Select(item => item.GetString()!).ToArray()
                        : [pair.Value.GetString()!], StringComparer.Ordinal)
                : new Dictionary<string, string[]>(StringComparer.Ordinal);
            result[property.Name] = new Scenario(
                value.GetProperty("text").GetString()!,
                cursor,
                value.GetProperty("width").GetInt32(),
                value.GetProperty("rows").GetInt32(),
                value.GetProperty("steps").EnumerateArray().Select(step => step.GetString()!).ToArray(),
                value.TryGetProperty("history", out var history)
                    ? history.EnumerateArray().Select(item => item.GetString()!).ToArray()
                    : [],
                bindings,
                value.GetProperty("expected").EnumerateArray().Select(step => new Step(
                    step.GetProperty("text").GetString()!,
                    new Cursor(step.GetProperty("cursor").GetProperty("line").GetInt32(),
                        step.GetProperty("cursor").GetProperty("col").GetInt32()),
                    new Visual(step.GetProperty("visual").GetProperty("row").GetInt32(),
                        step.GetProperty("visual").GetProperty("column").GetInt32()),
                    new ViewportCursor(step.GetProperty("viewportCursor").GetProperty("row").GetInt32(),
                        step.GetProperty("viewportCursor").GetProperty("column").GetInt32()),
                    step.GetProperty("viewport").EnumerateArray().Select(row => row.GetString()!).ToArray(),
                    step.TryGetProperty("selection", out var selection) && selection.ValueKind == JsonValueKind.String
                        ? selection.GetString() : null)).ToArray());
        }
        return result;
    }
}
