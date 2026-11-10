using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class EditorCompletionTests
{
    [Fact]
    public async Task PiFuzzySlashOrderingMatchesThePinnedCatalogue()
    {
        var completion = new EditorAutocomplete(() => Path.GetTempPath());
        var suggestions = await completion.GetSuggestionsAsync("/s", 2, force: false, CancellationToken.None);

        Assert.NotNull(suggestions);
        Assert.Equal(EditorCompletionKind.Slash, suggestions!.Kind);
        Assert.Equal(["settings", "scoped-models", "share", "session", "resume"],
            suggestions.Items.Take(5).Select(item => item.Value));
    }

    [Fact]
    public async Task SlashTabAppliesPiTrailingSpaceAndClosesThePicker()
    {
        var editor = new TerminalEditor();
        await editor.HandleActiveInputAsync(new(null, "/hot"),
            (_, _, _) => Task.FromResult(true), () => [], () => { });

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));

        Assert.Equal("/hotkeys ", editor.Draft);
    }

    [Fact]
    public async Task SlashCompletionPickerNavigatesAndAppliesSelectedCandidate()
    {
        var editor = new TerminalEditor();
        editor.Prefill("/mod");

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/mod", editor.Draft);
        await SendEditorKeyAsync(editor, Key(ConsoleKey.DownArrow, '\0'));
        Assert.Equal("/mod", editor.Draft);
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/scoped-models ", editor.Draft);

        editor.Prefill("/mod");
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        await SendEditorKeyAsync(editor, Key(ConsoleKey.UpArrow, '\0'));
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/scoped-models ", editor.Draft);
    }

    [Fact]
    public async Task ShiftTabIsReservedForTheThinkingCycleInsteadOfBackwardCompletion()
    {
        var editor = new TerminalEditor();
        editor.Prefill("/mod");

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/mod", editor.Draft);

        string? action = null;
        Assert.True(await editor.HandleApplicationShortcutAsync(
            Key(ConsoleKey.Tab, '\t', ConsoleModifiers.Shift), dispatched =>
            {
                action = dispatched;
                return Task.CompletedTask;
            }));
        Assert.Equal("app.thinking.cycle", action);
        Assert.Equal("/mod", editor.Draft);
    }

    [Fact]
    public async Task BackspaceReopensAndNarrowsTheCompletionPickerLikePi()
    {
        var editor = new TerminalEditor();
        editor.Prefill("/mod");

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/mod", editor.Draft);
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Backspace, '\b'));
        Assert.Equal("/mo", editor.Draft);
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/model ", editor.Draft);
    }

    [Fact]
    public async Task DismissedCompletionReopensOnBackspaceLikePi()
    {
        var editor = new TerminalEditor();
        editor.Prefill("/model");

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Escape, '\u001b'));
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Backspace, '\b'));
        Assert.Equal("/mode", editor.Draft);
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/model ", editor.Draft);
    }

    [Fact]
    public async Task CursorMovementRequeriesTheOpenCompletionPickerLikePi()
    {
        var editor = new TerminalEditor();
        editor.Prefill("/mod");

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        await SendEditorKeyAsync(editor, Key(ConsoleKey.LeftArrow, '\0'));
        await SendEditorKeyAsync(editor, Key(ConsoleKey.RightArrow, '\0'));
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/model ", editor.Draft);
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
        Assert.Equal("/thi", editor.Draft);
        Assert.Empty(submissions);

        await SendEditorKeyAsync(editor, Key(ConsoleKey.Enter, '\r'), (text, _, _) =>
        {
            submissions.Add(text);
            return Task.FromResult(true);
        });
        Assert.Equal(["/thinking "], submissions);
        Assert.Equal("", editor.Draft);

        editor.Prefill("/does-not-exist");
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
        Assert.Equal("/does-not-exist", editor.Draft);
    }

    [Fact]
    public async Task ThinkingArgumentCompletionMatchesPinnedPiLevels()
    {
        var editor = new TerminalEditor(getThinkingLevels: () => ["off", "minimal", "low", "medium", "high"]);

        await SendTextAsync(editor, "/thinking mi");
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));

        Assert.Equal("/thinking minimal", editor.Draft);
    }

    [Fact]
    public async Task ModelArgumentCompletionPreservesTheCommandPrefix()
    {
        var editor = new TerminalEditor(
            getModels: () => [new EditorModelOption("fixture", "fixture-model", "Fixture Model")]);

        await SendTextAsync(editor, "/model fix");
        await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));

        Assert.Equal("/model fixture/fixture-model", editor.Draft);
    }

    [Fact]
    public async Task LoginArgumentCompletionDeduplicatesProvidersAndRanksByName()
    {
        var completion = new EditorAutocomplete(() => Path.GetTempPath(),
            getLoginProviders: () =>
            [
                new EditorLoginProvider("anthropic", "Anthropic", OAuthSupported: true, ApiKeySupported: false),
                new EditorLoginProvider("anthropic", "Anthropic", OAuthSupported: false, ApiKeySupported: true),
                new EditorLoginProvider("azure", "Azure", OAuthSupported: false, ApiKeySupported: true)
            ]);

        var all = await completion.GetSuggestionsAsync("/login ", 7, force: false, CancellationToken.None);
        Assert.NotNull(all);
        Assert.Equal(EditorCompletionKind.Argument, all!.Kind);
        Assert.Equal(["anthropic", "azure"], all.Items.Select(item => item.Value));
        Assert.Equal("Anthropic · subscription/API key", all.Items[0].Description);

        var fuzzy = await completion.GetSuggestionsAsync("/login anth", 11, force: false, CancellationToken.None);
        Assert.NotNull(fuzzy);
        Assert.Equal(["anthropic"], fuzzy!.Items.Select(item => item.Value));
    }

    [Fact]
    public void AtAttachmentCompletionAddsPiTrailingSpaceAndKeepsSurroundingProse()
    {
        var completion = new EditorAutocomplete(() => Path.GetTempPath());
        var buffer = new EditorBuffer();
        buffer.SetText("see @sp now");
        buffer.SetCursor(7);
        var item = new EditorCompletionItem("@\"space name.txt\"", "space name.txt", "space name.txt",
            IsDirectory: false, IsSlash: false, IsArgument: false, IsAt: true);

        completion.Apply(buffer, new EditorCompletionResult(EditorCompletionKind.Path, "@sp", [item]), item);

        Assert.Equal("see @\"space name.txt\"  now", buffer.Text);
        Assert.Equal("see @\"space name.txt\" ".Length, buffer.Cursor);
    }

    [Fact]
    public void UnprefixedTabCompletionMatchesPiWithoutATrailingSpace()
    {
        var completion = new EditorAutocomplete(() => Path.GetTempPath());
        var buffer = new EditorBuffer();
        buffer.SetText("B");
        buffer.SetCursor(1);
        var item = new EditorCompletionItem("Beta.txt", "Beta.txt", "Beta.txt",
            IsDirectory: false, IsSlash: false, IsArgument: false, IsAt: false);

        completion.Apply(buffer, new EditorCompletionResult(EditorCompletionKind.Path, "B", [item]), item);

        Assert.Equal("Beta.txt", buffer.Text);
        Assert.Equal(8, buffer.Cursor);
    }

    [Fact]
    public void DirectoryCompletionKeepsTheCursorBeforeItsClosingQuote()
    {
        var completion = new EditorAutocomplete(() => Path.GetTempPath());
        var buffer = new EditorBuffer();
        buffer.SetText("@\"my folder/\"");
        buffer.SetCursor(buffer.Text.Length - 1);
        var item = new EditorCompletionItem("@\"my folder/\"", "my folder/", null,
            IsDirectory: true, IsSlash: false, IsArgument: false, IsAt: true);

        completion.Apply(buffer, new EditorCompletionResult(EditorCompletionKind.Path, "@\"my folder/", [item]), item);

        Assert.Equal("@\"my folder/\"", buffer.Text);
        Assert.Equal(buffer.Text.Length - 1, buffer.Cursor);
    }

    [Fact]
    public async Task ForcedTabCompletionStaysOpenWhileTypingLikePi()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-complete-force-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Beta.txt"), "upper");
        File.WriteAllText(Path.Combine(root, "beta2.txt"), "lower");
        try
        {
            var editor = new TerminalEditor(getWorkingDirectory: () => root);
            await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));
            await SendTextAsync(editor, "B");
            await SendEditorKeyAsync(editor, Key(ConsoleKey.Tab, '\t'));

            Assert.Equal("Beta.txt", editor.Draft);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SlashCompletionSurfaceComesFromTheLiveRegistration()
    {
        var completion = new EditorAutocomplete(() => Path.GetTempPath(),
            dynamicCommands: () => ["/skill:demo", "/review"]);

        var suggestions = await completion.GetSuggestionsAsync("/skill:d", 8, force: false, CancellationToken.None);

        Assert.NotNull(suggestions);
        Assert.Equal(["skill:demo"], suggestions!.Items.Select(item => item.Value));
    }

    private static Task<bool> SendTextAsync(TerminalEditor editor, string text) =>
        editor.HandleActiveInputAsync(new(null, text), (_, _, _) => Task.FromResult(true),
            () => [], () => { });

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
    [InlineData("    /tr", "    /tree")]
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
