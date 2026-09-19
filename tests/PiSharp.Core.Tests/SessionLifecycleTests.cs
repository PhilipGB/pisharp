using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

/// <summary>
/// Session lifecycle parity: the unsaved fork/clone guard, fork-before-user-message semantics,
/// /import copy-into-session-dir behavior, the missing-cwd prompt, cross-project
/// --session resolution with the fork-into-current-directory prompt, --session-id, and the
/// --fork/--session-id CLI conflicts.
/// </summary>
public sealed class SessionLifecycleTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // --- fork / clone ----------------------------------------------------------

    [Fact]
    public async Task CloneIsRefusedBeforeTheFirstAssistantMessage()
    {
        using var temp = TempDirectory.Create();
        var (controller, _) = await Harness.CreateControllerAsync(temp);

        // Only a user prompt so far: under the pinned lazy-flush contract the file is
        // not materialized yet, so fork/clone is refused with the pinned message.
        await controller.PersistUserMessageAsync("first prompt", null, CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ForkAsync(null, CancellationToken.None));
        Assert.Equal("This session has not been saved yet. Wait for the first assistant response before cloning or forking it.", error.Message);

        error = await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ForkAsync(null, CancellationToken.None));
        Assert.Equal("This session has not been saved yet. Wait for the first assistant response before cloning or forking it.", error.Message);
    }

    [Fact]
    public async Task ForkBeforeAUserMessageExcludesTheEntryAndForksAtItsParent()
    {
        using var temp = TempDirectory.Create();
        var (controller, _) = await Harness.CreateControllerAsync(temp);

        await controller.PersistUserMessageAsync("first", null, CancellationToken.None);
        await controller.PersistAssistantMessagesAsync([Message("first answer")], CancellationToken.None);
        await controller.PersistUserMessageAsync("second", null, CancellationToken.None);
        var second = (MessageEntry)controller.Document!.GetActiveEntryPath(controller.ActiveEntryId!)[^1];

        var sourcePath = controller.Document!.FilePath;
        await controller.ForkAsync(second.Id, CancellationToken.None);

        var forked = controller.Document!;
        Assert.NotEqual(sourcePath, forked.FilePath);
        // The fork contains the branch up to the pending entry's parent — "second" itself
        // is excluded (pinned position "before"). The startup model resolution may append
        // a model_change entry on top of the copied branch.
        var forkedMessages = forked.Entries.OfType<MessageEntry>().ToList();
        Assert.Equal(2, forkedMessages.Count);
        Assert.All(forkedMessages, entry => Assert.NotEqual(second.Id, entry.Id));
        // The fork records its source in the header.
        Assert.Equal(sourcePath, forked.PiHeader!.ParentSession);
    }

    [Fact]
    public async Task ForkFromAnAssistantMessageIsRefused()
    {
        using var temp = TempDirectory.Create();
        var (controller, _) = await Harness.CreateControllerAsync(temp);

        await controller.PersistUserMessageAsync("first", null, CancellationToken.None);
        await controller.PersistAssistantMessagesAsync([Message("first answer")], CancellationToken.None);
        var assistant = (MessageEntry)controller.Document!.GetActiveEntryPath(controller.ActiveEntryId!)[^1];

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.ForkAsync(assistant.Id, CancellationToken.None));
        Assert.Equal("Can only fork from a user message.", error.Message);
    }

    // --- import ------------------------------------------------------------------

    [Fact]
    public async Task ImportCopiesTheFileIntoTheSessionDirectoryAndActivatesIt()
    {
        using var temp = TempDirectory.Create();
        var (controller, options) = await Harness.CreateControllerAsync(temp);
        var workspace = options.WorkingDirectory;

        // A foreign session file outside the session directory.
        var externalDir = Path.Combine(temp.Path, "external");
        Directory.CreateDirectory(externalDir);
        var source = Path.Combine(externalDir, "imported.jsonl");
        await WriteForeignSessionAsync(source, workspace, "imp1", "imported prompt");

        await controller.ImportAsync(source, CancellationToken.None);

        // The file was copied into the session directory and activated.
        var destination = Path.Combine(controller.StoreDirectory!, "imported.jsonl");
        Assert.True(File.Exists(destination));
        Assert.Equal(destination, controller.Document!.FilePath);
        Assert.Equal("imp1", controller.Document.PiHeader!.Id);

        // A second import of the same file gets the -1 suffix (pinned collision handling).
        await controller.ImportAsync(source, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(controller.StoreDirectory!, "imported-1.jsonl")));
    }

    [Fact]
    public async Task ImportOfAMissingStoredCwdPromptsToContinueInTheCurrentCwd()
    {
        using var temp = TempDirectory.Create();
        var console = new FakeConsoleIO();
        console.EnqueueText("y\n");
        var (controller, options) = await Harness.CreateControllerAsync(temp, console);
        var workspace = options.WorkingDirectory;

        var externalDir = Path.Combine(temp.Path, "external");
        Directory.CreateDirectory(externalDir);
        var source = Path.Combine(externalDir, "orphan.jsonl");
        var missingCwd = Path.Combine(temp.Path, "gone");
        await WriteForeignSessionAsync(source, missingCwd, "imp2", "orphan prompt");

        await controller.ImportAsync(source, CancellationToken.None);

        Assert.Contains("does not exist", console.Output);
        Assert.Contains("Continue in the current directory? [y/N]", console.Output);
        Assert.Equal("imp2", controller.Document!.PiHeader!.Id);
        _ = workspace;
    }

    [Fact]
    public async Task ImportOfAMissingStoredCwdCancelsOnNo()
    {
        using var temp = TempDirectory.Create();
        var console = new FakeConsoleIO();
        console.EnqueueText("n\n");
        var (controller, _) = await Harness.CreateControllerAsync(temp, console);

        var externalDir = Path.Combine(temp.Path, "external");
        Directory.CreateDirectory(externalDir);
        var source = Path.Combine(externalDir, "orphan.jsonl");
        await WriteForeignSessionAsync(source, Path.Combine(temp.Path, "gone"), "imp3", "orphan");

        var before = controller.Document!.FilePath;
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => controller.ImportAsync(source, CancellationToken.None));
        Assert.Equal("Aborted.", error.Message);
        Assert.Equal(before, controller.Document.FilePath);
    }

    [Fact]
    public async Task ImportFailsWhenTheFileIsMissing()
    {
        using var temp = TempDirectory.Create();
        var (controller, _) = await Harness.CreateControllerAsync(temp);

        var error = await Assert.ThrowsAsync<FileNotFoundException>(
            () => controller.ImportAsync(Path.Combine(temp.Path, "nope.jsonl"), CancellationToken.None));
        Assert.Equal(Path.Combine(temp.Path, "nope.jsonl"), error.FileName);
    }

    // --- cross-project --session resolution --------------------------------------

    [Fact]
    public async Task CrossProjectSessionIsForkedIntoTheCurrentDirectoryAfterConfirmation()
    {
        using var temp = TempDirectory.Create();
        var sessionDir = Path.Combine(temp.Path, "sessions");

        // The foreign project: its sessions live in the same explicit session directory.
        var foreignCwd = Directory.CreateDirectory(Path.Combine(temp.Path, "foreign")).FullName;
        var foreignStore = new SessionStore(foreignCwd, sessionDir);
        var foreign = await foreignStore.CreatePiAsync(CancellationToken.None);
        await foreignStore.AppendEntriesAsync(foreign,
        [
            new MessageEntry("fu", null, DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { role = "user", content = "foreign prompt" })),
            new MessageEntry("fa", "fu", DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { role = "assistant", content = "foreign answer" })),
        ], CancellationToken.None);
        var foreignId = foreign.PiHeader!.Id;
        var foreignPath = foreign.FilePath;

        // The current project resolves the foreign id via the global search.
        var currentCwd = Directory.CreateDirectory(Path.Combine(temp.Path, "current")).FullName;
        var console = new FakeConsoleIO();
        console.EnqueueText("y\n");
        var (controller, _) = await Harness.CreateControllerAsync(
            temp, console, sessionDir: sessionDir, workingDirectory: currentCwd, sessionSelector: foreignId);

        Assert.Contains("Session found in different project", ConsoleOutput.Captured);
        var forked = controller.Document!;
        Assert.NotEqual(foreignPath, forked.FilePath);
        Assert.Equal(foreignPath, forked.PiHeader!.ParentSession);
        Assert.Equal(2, forked.Entries.OfType<MessageEntry>().Count());
        // The foreign source is untouched.
        Assert.Equal(2, (await new SessionStore(foreignCwd, sessionDir).LoadAsync(foreignPath)).Entries.Count);
    }

    [Fact]
    public async Task CrossProjectSessionAbortsWithoutConfirmation()
    {
        using var temp = TempDirectory.Create();
        var sessionDir = Path.Combine(temp.Path, "sessions");
        var foreignCwd = Directory.CreateDirectory(Path.Combine(temp.Path, "foreign")).FullName;
        var foreignStore = new SessionStore(foreignCwd, sessionDir);
        var foreign = await foreignStore.CreatePiAsync(CancellationToken.None);
        await foreignStore.AppendEntriesAsync(foreign,
        [
            new MessageEntry("fu", null, DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { role = "user", content = "foreign prompt" })),
            new MessageEntry("fa", "fu", DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { role = "assistant", content = "foreign answer" })),
        ], CancellationToken.None);

        var currentCwd = Directory.CreateDirectory(Path.Combine(temp.Path, "current")).FullName;
        var console = new FakeConsoleIO();
        console.EnqueueText("n\n");

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Harness.CreateControllerAsync(
                temp, console, sessionDir: sessionDir, workingDirectory: currentCwd, sessionSelector: foreign.PiHeader!.Id));
    }

    // --- --session-id and CLI conflicts -------------------------------------------

    [Fact]
    public async Task SessionIdCreatesANewSessionWithThatId()
    {
        using var temp = TempDirectory.Create();
        var (controller, _) = await Harness.CreateControllerAsync(temp, sessionNameId: "mysess");
        Assert.Equal("mysess", controller.Document!.PiHeader!.Id);
    }

    [Fact]
    public async Task SessionIdOpensAnExistingLocalSessionWhenTheIdMatches()
    {
        using var temp = TempDirectory.Create();
        var sessionDir = Path.Combine(temp.Path, "sessions");
        var cwd = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;

        // Pre-existing session with a known id.
        var store = new SessionStore(cwd, sessionDir);
        var existing = await store.CreatePiAsync(CancellationToken.None, null, "known-id");
        await store.AppendEntriesAsync(existing,
        [
            new MessageEntry("u", null, DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { role = "user", content = "hi" })),
            new MessageEntry("a", "u", DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { role = "assistant", content = "yo" })),
        ], CancellationToken.None);

        var (controller, _) = await Harness.CreateControllerAsync(
            temp, sessionDir: sessionDir, workingDirectory: cwd, sessionNameId: "known-id");
        Assert.Equal(existing.FilePath, controller.Document!.FilePath);
    }

    [Fact]
    public void ForkFlagConflictsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--fork", "x", "--session", "y"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--fork", "x", "--no-session"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--fork", "x", "--continue"]));
    }

    [Fact]
    public void SessionIdFlagConflictsAndValidation()
    {
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--session-id", "x", "--resume"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--session-id", "x", "--session", "y"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--session-id", "bad id!"]));
        Assert.Throws<ArgumentException>(() => CliOptions.Parse(["--session-id", "-leading"]));

        var ok = CliOptions.Parse(["--fork", "x", "--session-id", "good_id-1"]);
        Assert.Equal("x", ok.ForkSelector);
        Assert.Equal("good_id-1", ok.SessionId);
    }

    // --- helpers -------------------------------------------------------------------

    private static JsonElement Message(string text) =>
        JsonSerializer.SerializeToElement(new
        {
            role = "assistant",
            content = new object[] { new { type = "text", text } },
        });

    private static async Task WriteForeignSessionAsync(string path, string cwd, string id, string prompt)
    {
        var store = new SessionStore(cwd, Path.GetDirectoryName(path)!);
        var document = await store.CreatePiAsync(CancellationToken.None, null, id);
        await store.AppendEntriesAsync(document,
        [
            new MessageEntry("u", null, DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { role = "user", content = prompt })),
            new MessageEntry("a", "u", DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { role = "assistant", content = "answer" })),
        ], CancellationToken.None);

        // Move the file to the requested path (the store created it in its own directory).
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!string.Equals(document.FilePath, path, StringComparison.Ordinal))
        {
            File.Move(document.FilePath, path, overwrite: true);
        }
    }

    /// <summary>Minimal console-output capture for the cross-project prompt (printed via Console).</summary>
    private static class ConsoleOutput
    {
        public static string Captured { get; set; } = string.Empty;
    }

    private sealed class Harness
    {
        public static async Task<(SessionController Controller, CliOptions Options)> CreateControllerAsync(
            TempDirectory temp,
            IConsoleIO? console = null,
            string? sessionDir = null,
            string? workingDirectory = null,
            string? sessionSelector = null,
            string? sessionNameId = null)
        {
            var workspace = workingDirectory ?? Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
            sessionDir ??= Path.Combine(temp.Path, "sessions");

            var agent = new SessionOperationTests.RecordingAgent();
            var summarizer = new CompactionRuntimeTests.ScriptedLeafClient(
            [
                new CompactionRuntimeTests.LeafBehavior
                {
                    BuildUpdates = _ =>
                    [
                        new ChatResponseUpdate(ChatRole.Assistant, new AIContent[] { new TextContent("summary") }),
                    ],
                },
            ]);
            var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync();
            var bootstrap = new AgentBootstrap(
                Agent: agent,
                SummaryClient: summarizer,
                ContextFiles: [],
                Skills: [],
                PromptTemplates: [],
                ExtensionHost: new PiSharpExtensionHost(),
                RetryPolicy: RetryPolicyOptions.Disabled,
                TurnQueue: new TurnMessageQueue(),
                SessionHistory: new PiSessionChatHistoryProvider(),
                Compaction: new CompactionTarget(),
                ModelRuntime: runtime,
                ModelState: state);

            var options = new CliOptions(
                WorkingDirectory: workspace,
                Model: ModelRuntimeTestKit.Reference,
                Endpoint: null,
                ApiKey: null,
                Provider: null,
                Models: [],
                Thinking: null,
                ListModels: false,
                Offline: false,
                ContextTokens: 128_000,
                MaxOutputTokens: 1024,
                ContextTokensExplicit: false,
                MaxOutputTokensExplicit: false,
                Prompt: null,
                FilePaths: [],
                ShowHelp: false,
                ContinueSession: false,
                ResumeSession: false,
                SessionSelector: sessionSelector,
                SessionName: null,
                SessionDirectory: sessionDir,
                NoSession: false,
                ContextRoot: null,
                ExtensionPaths: [],
                SkillPaths: [],
                PromptTemplatePaths: [],
                NoExtensions: true,
                NoSkills: true,
                NoPromptTemplates: true,
                ProjectTrustOverride: true,
                OutputMode: OutputMode.Text,
                PrintMode: false,
                ReadOnly: false,
                NoTools: false,
                AutoRetry: false,
                ForkSelector: null,
                SessionId: sessionNameId);

            SessionController controller;
            if (sessionSelector is null)
            {
                controller = await SessionController.CreateAsync(bootstrap, options, CancellationToken.None, console: console);
            }
            else
            {
                // Capture the cross-project prompt line, which goes through Console.
                var writer = new StringWriter();
                var original = Console.Out;
                Console.SetOut(writer);
                try
                {
                    controller = await SessionController.CreateAsync(bootstrap, options, CancellationToken.None, console: console);
                }
                finally
                {
                    Console.SetOut(original);
                    ConsoleOutput.Captured = writer.ToString();
                }
            }

            return (controller, options);
        }
    }
}
