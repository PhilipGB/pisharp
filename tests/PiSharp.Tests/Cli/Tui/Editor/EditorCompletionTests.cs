using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class EditorCompletionTests
{
    [Fact]
    public async Task SlashCompletionCyclesCandidatesInTheEditorAndWrapsForward()
    {
        var editor = new TerminalEditor();
        editor.Prefill("/mod");

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/model", editor.Draft);
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/models", editor.Draft);
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/model", editor.Draft);
    }

    [Fact]
    public async Task ShiftTabCyclesBackwardAndWraps()
    {
        var editor = new TerminalEditor();
        editor.Prefill("/mod");

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t', ConsoleModifiers.Shift));
        Assert.Equal("/models", editor.Draft);
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t', ConsoleModifiers.Shift));
        Assert.Equal("/model", editor.Draft);
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t', ConsoleModifiers.Shift));
        Assert.Equal("/models", editor.Draft);
    }

    [Fact]
    public async Task EditingResetsTheCompletionCycle()
    {
        var editor = new TerminalEditor();
        editor.Prefill("/mod");

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/model", editor.Draft);
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Backspace, '\b'));
        Assert.Equal("/mode", editor.Draft);
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/model", editor.Draft);
    }

    [Fact]
    public async Task UnrelatedCursorInputResetsTheCompletionCycle()
    {
        var editor = new TerminalEditor();
        editor.Prefill("/mod");

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        await SendEditorKeyAsync(editor, Key(ConsoleKey.LeftArrow, '\0'));
        await SendEditorKeyAsync(editor, Key(ConsoleKey.RightArrow, '\0'));
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));

        Assert.Equal("/model", editor.Draft);
    }

    [Fact]
    public async Task UniqueCompletionIsSubmittedByTheFirstEnterAndNoMatchesAreANoop()
    {
        var submissions = new List<string>();
        var editor = new TerminalEditor(commands: () => ["/thinking"]);
        editor.Prefill("/thi");

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'), (text, _, _) =>
        {
            submissions.Add(text);
            return Task.FromResult(true);
        });
        Assert.Equal("/thinking", editor.Draft);
        Assert.Empty(submissions);

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Enter, '\r'), (text, _, _) =>
        {
            submissions.Add(text);
            return Task.FromResult(true);
        });
        Assert.Equal(["/thinking"], submissions);
        Assert.Equal("", editor.Draft);

        editor.Prefill("/does-not-exist");
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/does-not-exist", editor.Draft);
    }

    private static Task<bool> SendEditorKeyAsync(TerminalEditor editor, ConsoleKeyInfo key,
        Func<string, bool, CancellationToken, Task<bool>>? queue = null) =>
        editor.HandleActiveInputAsync(new(key, null), queue ?? ((_, _, _) => Task.FromResult(true)),
            () => [], () => { });

    private static ConsoleKeyInfo Key(ConsoleKey key, char character,
        ConsoleModifiers modifiers = ConsoleModifiers.None) =>
        new(character, key, modifiers.HasFlag(ConsoleModifiers.Shift), modifiers.HasFlag(ConsoleModifiers.Alt),
            modifiers.HasFlag(ConsoleModifiers.Control));

    [Fact]
    public void SlashCompletionShowsAmbiguityAndCompletesUniqueName()
    {
        var completion = new EditorCompletion(Path.GetTempPath());
        var buffer = new EditorBuffer();
        buffer.SetText("/tr");
        Assert.Equal(["/tree", "/trust"], completion.Complete(buffer));
        Assert.Equal("/tr", buffer.Text);
        buffer.SetText("/rel");
        Assert.Equal(["/reload"], completion.Complete(buffer));
        Assert.Equal("/reload", buffer.Text);
        buffer.SetText("Please /rel");
        Assert.Empty(completion.Complete(buffer));
    }

    [Fact]
    public void SlashCompletionIncludesLlamaRouterManager()
    {
        var completion = new EditorCompletion(Path.GetTempPath());
        var buffer = new EditorBuffer();
        buffer.SetText("/lla");

        Assert.Equal(["/llama"], completion.Complete(buffer));
    }

    [Fact]
    public void SlashCompletionCoversCanonicalBuiltInsAndKeepsDynamicCommands()
    {
        var dynamicCommands = new[]
        {
            new InteractiveCommandMetadata("/skill:local", "Local skill"),
            new InteractiveCommandMetadata("/review", "Prompt command"),
            new InteractiveCommandMetadata("/fixture", "Extension command"),
            new InteractiveCommandMetadata("/model", "Duplicate built-in")
        };
        var completion = new EditorCompletion(Path.GetTempPath(),
            () => dynamicCommands.Select(command => command.Command).ToArray(),
            command => dynamicCommands.FirstOrDefault(item => item.Command == command)?.Description);

        foreach (var command in InteractiveCommandCatalog.BuiltIns)
        {
            var buffer = new EditorBuffer();
            var uniquePrefix = Enumerable.Range(1, command.Command.Length)
                .Select(length => command.Command[..length])
                .FirstOrDefault(prefix => InteractiveCommandCatalog.BuiltIns.Count(candidate =>
                    candidate.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) == 1)
                ?? command.Command;
            buffer.SetText(uniquePrefix);
            Assert.Contains(command.Command, completion.Complete(buffer));
        }

        foreach (var command in new[]
                 { "/thinking", "/login", "/logout", "/copy", "/scoped-models", "/settings", "/model", "/models", "/mcp" })
        {
            var buffer = new EditorBuffer();
            buffer.SetText(command);
            Assert.Contains(command, completion.Complete(buffer));
        }

        foreach (var dynamicCommand in dynamicCommands.Take(3))
        {
            var dynamicBuffer = new EditorBuffer();
            dynamicBuffer.SetText(dynamicCommand.Command);
            Assert.Contains(dynamicCommand.Command, completion.Complete(dynamicBuffer));
        }

        var duplicateBuffer = new EditorBuffer();
        duplicateBuffer.SetText("/model");
        Assert.Equal(1, completion.Complete(duplicateBuffer).Count(command =>
            command.Equals("/model", StringComparison.OrdinalIgnoreCase)));

        var allBuffer = new EditorBuffer();
        allBuffer.SetText("/");
        Assert.Equal(InteractiveCommandCatalog.BuiltIns.Select(command => command.Command)
                .Concat(dynamicCommands.Take(3).Select(command => command.Command)),
            completion.Complete(allBuffer));
    }

    [Theory]
    [InlineData(" /tr", " /tree")]
    [InlineData("  /tr", "  /tree")]
    [InlineData("\t/tr", "\t/tree")]
    public void SlashCompletionAfterLeadingWhitespacePreservesIndentation(string prompt, string expected)
    {
        var completion = new EditorCompletion(Path.GetTempPath());
        var buffer = new EditorBuffer();
        buffer.SetText(prompt);

        Assert.Equal(["/tree", "/trust"], completion.Complete(buffer));
        Assert.Equal(prompt, buffer.Text);

        completion.ApplySelected(buffer, "/tree");
        Assert.Equal(expected, buffer.Text);
        Assert.Equal(expected.Length, buffer.Cursor);
    }

    [Fact]
    public void SlashCompletionReadsDynamicSkillCommandRegistration()
    {
        var enabled = true;
        var completion = new EditorCompletion(Path.GetTempPath(),
            () => enabled ? ["/skill:explain"] : []);
        var buffer = new EditorBuffer();
        buffer.SetText("/skill:");

        Assert.Equal(["/skill:explain"], completion.Complete(buffer));
        enabled = false;
        buffer.SetText("/skill:");
        Assert.Empty(completion.Complete(buffer));

        enabled = true;
        buffer.SetText("/skill:");
        Assert.Equal(["/skill:explain"], completion.Complete(buffer));
    }

    [Fact]
    public void SlashCommandSuggestionsTrackReloadedCommandsWithoutChangingTheDraft()
    {
        var commands = Array.Empty<string>();
        var descriptions = new Dictionary<string, string>();
        var completion = new EditorCompletion(Path.GetTempPath(), () => commands,
            command => descriptions.GetValueOrDefault(command));
        var buffer = new EditorBuffer();
        buffer.SetText("/skill:");

        Assert.Empty(completion.GetSlashCommandSuggestions(buffer));
        commands = ["/skill:reloaded-legacy-skill"];
        descriptions[commands[0]] = "[p] Reloaded legacy skill";
        Assert.Equal([new EditorCompletionSuggestion("/skill:reloaded-legacy-skill", "[p] Reloaded legacy skill")],
            completion.GetSlashCommandSuggestions(buffer));
        Assert.Equal("/skill:", buffer.Text);
        Assert.Equal(buffer.Text.Length, buffer.Cursor);
    }

    [Fact]
    public void EmptyPromptCanChooseAPathCompletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-complete-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "notes.txt"), "x");
        try
        {
            var completion = new EditorCompletion(root);
            var buffer = new EditorBuffer();

            var matches = completion.Complete(buffer);
            Assert.Equal(["docs/", "notes.txt"], matches);
            Assert.Equal("", buffer.Text);
            completion.ApplySelected(buffer, "notes.txt");
            Assert.Equal("notes.txt", buffer.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PathCompletionFollowsTheActiveProjectWorkingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-complete-projects-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "first.txt"), "x");
        File.WriteAllText(Path.Combine(second, "second.txt"), "x");
        try
        {
            var currentDirectory = first;
            var completion = new EditorCompletion(() => currentDirectory);
            var buffer = new EditorBuffer();

            Assert.Equal(["first.txt"], completion.Complete(buffer));
            currentDirectory = second;
            buffer.SetText("");
            Assert.Equal(["second.txt"], completion.Complete(buffer));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PathCompletionIncludesHiddenEntriesExceptGit()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-complete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            Directory.CreateDirectory(Path.Combine(root, ".pi"));
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.WriteAllText(Path.Combine(root, "data.txt"), "x");
            File.WriteAllText(Path.Combine(root, ".secret"), "x");
            var completion = new EditorCompletion(root);
            var buffer = new EditorBuffer();
            buffer.SetText("look @do");
            Assert.Equal(["@docs/"], completion.Complete(buffer));
            Assert.Equal("look @docs/", buffer.Text);
            buffer.SetText("@.");
            Assert.Equal(["@.pi/", "@.secret"], completion.Complete(buffer));
            buffer.SetText("@");
            Assert.Equal(["@.pi/", "@docs/", "@.secret", "@data.txt"], completion.Complete(buffer));
            Assert.DoesNotContain("@.git/", completion.Complete(buffer));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PathCompletionHandlesNestedUnicodePathsAndOpeningWrappers()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-complete-unicode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "文档"));
        Directory.CreateDirectory(Path.Combine(root, "[slug]"));
        Directory.CreateDirectory(Path.Combine(root, "😀-one"));
        Directory.CreateDirectory(Path.Combine(root, "😀-two"));
        File.WriteAllText(Path.Combine(root, "src", "main.ts"), "x");
        File.WriteAllText(Path.Combine(root, "文档", "说明.md"), "x");
        File.WriteAllText(Path.Combine(root, "[slug]", "page.tsx"), "x");
        try
        {
            var completion = new EditorCompletion(root);
            var buffer = new EditorBuffer();
            buffer.SetText("See (`src/ma");
            Assert.Equal(["src/main.ts"], completion.Complete(buffer));
            Assert.Equal("See (`src/main.ts", buffer.Text);

            buffer.SetText("查看，@文档/说");
            Assert.Equal(["@文档/说明.md"], completion.Complete(buffer));
            Assert.Equal("查看，@文档/说明.md", buffer.Text);

            buffer.SetText("查看『@文档/说");
            Assert.Equal(["@文档/说明.md"], completion.Complete(buffer));
            Assert.Equal("查看『@文档/说明.md", buffer.Text);

            buffer.SetText("([slug]/pa");
            Assert.Equal(["[slug]/page.tsx"], completion.Complete(buffer));
            Assert.Equal("([slug]/page.tsx", buffer.Text);

            buffer.SetText("@😀");
            Assert.Equal(["@😀-one/", "@😀-two/"], completion.Complete(buffer));
            Assert.Equal("@😀-", buffer.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void QuotedDirectoryCompletionContinuesWithoutDuplicatingItsClosingQuote()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-complete-quoted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "my folder"));
        File.WriteAllText(Path.Combine(root, "my folder", "notes one.md"), "x");
        try
        {
            var completion = new EditorCompletion(root);
            var buffer = new EditorBuffer();
            buffer.SetText("@my");
            Assert.Equal(["@\"my folder/\""], completion.Complete(buffer));
            Assert.Equal("@\"my folder/\"", buffer.Text);
            Assert.Equal(buffer.Text.Length - 1, buffer.Cursor);

            Assert.Equal(["@\"my folder/notes one.md\""], completion.Complete(buffer));
            Assert.Equal("@\"my folder/notes one.md\"", buffer.Text);
            Assert.Equal(buffer.Text.Length - 1, buffer.Cursor);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
