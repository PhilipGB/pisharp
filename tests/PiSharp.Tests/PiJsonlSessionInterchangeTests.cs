using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class PiJsonlSessionInterchangeTests
{
    [Fact]
    public void SessionNameChangeIsAnActivePiEntryAndSurvivesExportAndImport()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture-model", null, "fixture");
        session.Append(new ChatMessage(ChatRole.User, "hello"));
        var parentId = session.Tree.HeadId;
        using (var change = session.BeginSessionNameChange("project notes")) change.Commit();

        var nameEntry = Assert.Single(session.Tree.Entries, entry => entry.Type == "session_info");
        Assert.Equal(parentId, nameEntry.ParentId);
        var exported = PiJsonlSessionInterchange.Export(session);
        using var record = JsonDocument.Parse(exported.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("session_info", StringComparison.Ordinal)));
        Assert.Equal(nameEntry.Id, record.RootElement.GetProperty("id").GetString());
        Assert.Equal(parentId, record.RootElement.GetProperty("parentId").GetString());
        Assert.Equal("project notes", record.RootElement.GetProperty("name").GetString());

        var imported = PiJsonlSessionInterchange.Import(exported);
        Assert.Equal("project notes", imported.Name);
        Assert.Equal("session_info", imported.Tree.Entries[^1].Type);
    }

    [Fact]
    public void ParentSessionPathRoundTripsThroughNativeAndPiSessionHeaders()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        const string parentPath = "/sessions/parent.jsonl";
        var child = new ConversationSession(cwd, "fixture-model", null, "fixture", parentPath);

        var native = ConversationSession.Parse(child.ToJson());
        var imported = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(native));

        Assert.Equal(parentPath, native.ParentSessionPath);
        Assert.Equal(parentPath, imported.ParentSessionPath);
        using var header = JsonDocument.Parse(PiJsonlSessionInterchange.Export(imported)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal(parentPath, header.RootElement.GetProperty("parentSession").GetString());
    }

    [Fact]
    public void RpcMessageProjectionUsesOnlyNewCanonicalMessagesAndKeepsToolCallIdentity()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture-model", null, "fixture");
        session.Append(new ChatMessage(ChatRole.User, "earlier"));
        var runStart = session.Tree.HeadId;
        session.Append(new ChatMessage(ChatRole.User, "new prompt"));
        session.Append(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "read",
            new Dictionary<string, object?> { ["path"] = "file.txt" })]));
        session.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "contents")]));

        var messages = PiJsonlSessionInterchange.ProjectRunMessages(session, runStart, "openai-completions");

        Assert.Equal(3, messages.Count);
        Assert.Equal("new prompt", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("toolUse", messages[1]!["stopReason"]!.GetValue<string>());
        Assert.Equal("openai-completions", messages[1]!["api"]!.GetValue<string>());
        Assert.Equal("read", messages[2]!["toolName"]!.GetValue<string>());
        Assert.Equal("call-1", messages[2]!["toolCallId"]!.GetValue<string>());
    }

    [Fact]
    public void InterruptedRunProjectionUsesThePiAbortErrorForExistingAndSynthesizedAssistantMessages()
    {
        var withAssistant = new ConversationSession(Path.GetTempPath(), "fixture-model", null, "fixture");
        withAssistant.Append(new ChatMessage(ChatRole.User, "prompt"));
        var runStart = withAssistant.Tree.HeadId;
        withAssistant.Append(new ChatMessage(ChatRole.Assistant, "partial"));
        withAssistant.Tree.Append("interrupted", JsonSerializer.SerializeToElement(new
        {
            partialAssistantText = "partial"
        }));

        var projectedAssistant = Assert.Single(PiJsonlSessionInterchange.ProjectRunMessages(
            withAssistant, runStart, "openai-completions", "turn_interrupted").OfType<System.Text.Json.Nodes.JsonObject>(),
            message => message["role"]?.GetValue<string>() == "assistant");
        Assert.Equal("aborted", projectedAssistant["stopReason"]?.GetValue<string>());
        Assert.Equal("Request was aborted", projectedAssistant["errorMessage"]?.GetValue<string>());

        var withoutAssistant = new ConversationSession(Path.GetTempPath(), "fixture-model", null, "fixture");
        withoutAssistant.Append(new ChatMessage(ChatRole.User, "prompt"));
        var emptyRunStart = withoutAssistant.Tree.HeadId;
        withoutAssistant.Tree.Append("interrupted", JsonSerializer.SerializeToElement(new
        {
            partialAssistantText = ""
        }));

        var synthesizedAssistant = Assert.Single(PiJsonlSessionInterchange.ProjectRunMessages(
            withoutAssistant, emptyRunStart, "openai-completions", "turn_interrupted").OfType<System.Text.Json.Nodes.JsonObject>(),
            message => message["role"]?.GetValue<string>() == "assistant");
        Assert.Equal("aborted", synthesizedAssistant["stopReason"]?.GetValue<string>());
        Assert.Equal("Request was aborted", synthesizedAssistant["errorMessage"]?.GetValue<string>());
    }

    [Fact]
    public void InterruptedPartialIsPersistedAsAnAbortedPiMessageAndRestoredIntoModelContext()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture-model", null, "fixture");
        session.Append(new ChatMessage(ChatRole.User, "prompt"));
        var parentId = session.Tree.HeadId;
        var interrupted = session.Tree.Append("interrupted", JsonSerializer.SerializeToElement(new
        {
            prompt = "prompt",
            partialText = "partial output",
            partialAssistantText = "partial output",
            terminalType = "turn_interrupted",
            stopReason = "aborted",
            provider = "fixture",
            model = "fixture-model",
            timestamp = DateTimeOffset.UtcNow
        }));

        var entry = Assert.Single(PiJsonlSessionInterchange.ProjectEntries(session),
            item => item.GetProperty("id").GetString() == interrupted.Id);
        Assert.Equal("message", entry.GetProperty("type").GetString());
        Assert.Equal(parentId, entry.GetProperty("parentId").GetString());
        var assistant = entry.GetProperty("message");
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("partial output", assistant.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("aborted", assistant.GetProperty("stopReason").GetString());
        Assert.Equal("Request was aborted", assistant.GetProperty("errorMessage").GetString());
        Assert.Equal([ChatRole.User, ChatRole.Assistant], session.ContextMessages().Select(message => message.Role));
        Assert.Equal("partial output", session.ContextMessages()[1].Text);

        var roundTrip = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(session));
        Assert.Equal(session.ContextMessages().Select(message => message.Text),
            roundTrip.ContextMessages().Select(message => message.Text));
    }

    [Fact]
    public void InterruptedProjectionDoesNotDuplicateAnAlreadyPersistedPartialAssistant()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture-model", null, "fixture");
        session.Append(new ChatMessage(ChatRole.User, "earlier prompt"));
        session.Append(new ChatMessage(ChatRole.Assistant, "earlier answer"));
        session.Append(new ChatMessage(ChatRole.User, "prompt"));
        session.Append(new ChatMessage(ChatRole.Assistant, "partial output"));
        session.Tree.Append("interrupted", JsonSerializer.SerializeToElement(new
        {
            partialAssistantText = "partial output"
        }));

        Assert.Equal(["earlier prompt", "earlier answer", "prompt", "partial output"],
            session.ContextMessages().Select(message => message.Text));
        var messages = RpcSessionMessageProjector.Project(session, "openai-completions");
        Assert.Equal(2, messages
            .OfType<System.Text.Json.Nodes.JsonObject>()
            .Count(message => message["role"]?.GetValue<string>() == "assistant"));
        Assert.Equal("aborted", messages.OfType<System.Text.Json.Nodes.JsonObject>()
            .Last(message => message["role"]?.GetValue<string>() == "assistant")["stopReason"]?.GetValue<string>());
        var plan = Assert.IsType<ConversationSession.CompactionPlan>(session.PrepareCompaction());
        Assert.Equal(session.Tree.ActivePath()[2].Id, plan.FirstKeptEntryId);
    }

    [Fact]
    public void NativeContextOmissionsFilterModelInputAndRoundTripAsPiEntries()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture-model", null);
        session.Append(new ChatMessage(ChatRole.User, "hello"));
        session.Append(new ChatMessage(ChatRole.Assistant, "temporary failure"));
        var failedAssistantId = session.Tree.HeadId!;
        session.AppendContextOmission(failedAssistantId);

        Assert.Equal([ChatRole.User], session.ContextMessages().Select(message => message.Role));
        var exported = PiJsonlSessionInterchange.Export(session);
        var editLine = Assert.Single(exported.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => line.Contains("\"type\":\"context_edit\"", StringComparison.Ordinal));
        using var edit = JsonDocument.Parse(editLine);
        Assert.Equal(failedAssistantId, edit.RootElement.GetProperty("targetId").GetString());
        Assert.Equal(JsonValueKind.Null, edit.RootElement.GetProperty("replacement").ValueKind);

        var reloaded = PiJsonlSessionInterchange.Import(exported);
        Assert.Equal([ChatRole.User], reloaded.ContextMessages().Select(message => message.Role));
        Assert.Equal("temporary failure", reloaded.ActiveMessages().Last(message => message.Role == ChatRole.Assistant).Text);
    }

    [Fact]
    public void CurrentPiV3ImportPreservesBranchesContextEditsCompactionAndOriginalRecords()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var jsonl = V3Session(cwd);

        var imported = PiJsonlSessionInterchange.Import(jsonl);
        Assert.Equal("session-v3", imported.Id);
        Assert.Equal("claude-sonnet-4-5", imported.Model);
        Assert.Equal("anthropic", imported.Provider);
        Assert.Equal(10, imported.Tree.Entries.Count);
        Assert.Equal(["instructions", "alternate", "The following is a summary of a branch that this conversation came back from:\n\n<summary>\nold path\n</summary>", "branch prompt"],
            imported.ContextMessages().Select(message => message.Text));

        imported.Tree.Select("kept-next");
        Assert.Equal([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User],
            imported.ContextMessages().Select(message => message.Role));
        Assert.Equal("checkpoint", imported.ContextMessages()[0].Text);
        Assert.Contains("prior summary", imported.ContextMessages()[1].Text);
        Assert.Equal("edited", imported.ContextMessages()[2].Text);

        var exported = PiJsonlSessionInterchange.Export(imported);
        var records = exported.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.Equal(3, records[0].GetProperty("version").GetInt32());
        Assert.Equal("a-root", records[1].GetProperty("id").GetString());
        Assert.Equal("a-root", records[2].GetProperty("parentId").GetString());
        Assert.Equal("kept-next", records[^1].GetProperty("id").GetString());
        Assert.Equal("edited", records.Single(entry => entry.GetProperty("id").GetString() == "edit-a")
            .GetProperty("replacement").GetProperty("content").GetString());

        var roundTripped = PiJsonlSessionInterchange.Import(exported);
        Assert.Equal("kept-next", roundTripped.Tree.HeadId);
        Assert.Equal(imported.ContextMessages().Select(message => message.Role),
            roundTripped.ContextMessages().Select(message => message.Role));
        Assert.Equal(imported.ContextMessages().Select(message => message.Text),
            roundTripped.ContextMessages().Select(message => message.Text));
    }

    [Fact]
    public void LegacyV1AndV2SessionsMigrateToLinkedCurrentEntries()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        const string v1 = """
            {"type":"session","id":"legacy-v1","timestamp":"2024-12-03T14:00:00.000Z","cwd":"CWD"}
            {"type":"message","timestamp":"2024-12-03T14:00:01.000Z","message":{"role":"user","content":"hello","timestamp":1733234401000}}
            {"type":"message","timestamp":"2024-12-03T14:00:02.000Z","message":{"role":"hookMessage","content":"legacy","timestamp":1733234402000}}
            """;
        var migratedV1 = PiJsonlSessionInterchange.Import(v1.Replace("CWD", cwd, StringComparison.Ordinal));
        Assert.Equal(["hello", "legacy"], migratedV1.ContextMessages().Select(message => message.Text));
        Assert.Null(migratedV1.Tree.Entries[0].ParentId);
        Assert.Equal(migratedV1.Tree.Entries[0].Id, migratedV1.Tree.Entries[1].ParentId);
        Assert.Equal(3, JsonDocument.Parse(PiJsonlSessionInterchange.Export(migratedV1).Split('\n')[0])
            .RootElement.GetProperty("version").GetInt32());

        var compactedV1 = $$$"""
            {"type":"session","id":"legacy-v1-compacted","timestamp":"2024-12-03T14:00:00.000Z","cwd":"{{{cwd}}}"}
            {"type":"message","timestamp":"2024-12-03T14:00:01.000Z","message":{"role":"user","content":"kept boundary","timestamp":1733234401000}}
            {"type":"compaction","timestamp":"2024-12-03T14:00:02.000Z","summary":"legacy summary","firstKeptEntryIndex":1,"tokensBefore":200}
            {"type":"message","timestamp":"2024-12-03T14:00:03.000Z","message":{"role":"user","content":"after compaction","timestamp":1733234403000}}
            """;
        var migratedCompaction = PiJsonlSessionInterchange.Import(compactedV1);
        var compactedNode = Assert.Single(migratedCompaction.Tree.Entries, entry => entry.Type == "compaction");
        Assert.Equal(migratedCompaction.Tree.Entries[0].Id,
            compactedNode.Payload.GetProperty("firstKeptEntryId").GetString());
        Assert.Contains("kept boundary", migratedCompaction.ContextMessages().Select(message => message.Text));

        var v2 = $$$"""
            {"type":"session","version":2,"id":"legacy-v2","timestamp":"2024-12-03T14:00:00.000Z","cwd":"{{{cwd}}}"}
            {"type":"message","id":"before","parentId":null,"timestamp":"2024-12-03T14:00:01.000Z","message":{"role":"user","content":"hello","timestamp":1733234401000}}
            {"type":"message","id":"after","parentId":"before","timestamp":"2024-12-03T14:00:02.000Z","message":{"role":"hookMessage","content":"legacy","timestamp":1733234402000}}
            """;
        var migratedV2 = PiJsonlSessionInterchange.Import(v2);
        Assert.Equal("after", migratedV2.Tree.HeadId);
        Assert.Equal("before", migratedV2.Tree.Entries[1].ParentId);
        Assert.Equal("legacy", migratedV2.ActiveMessages()[1].Text);
    }

    [Fact]
    public void ImportedToolCallsImagesAndErrorsRemainAvailableToMafAndExport()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var jsonl = $$$"""
            {"type":"session","version":3,"id":"media-session","timestamp":"2025-01-01T00:00:00Z","cwd":"{{{cwd}}}"}
            {"type":"message","id":"user","parentId":null,"timestamp":"2025-01-01T00:00:01Z","message":{"role":"user","content":[{"type":"text","text":"inspect"},{"type":"image","data":"AQID","mimeType":"image/png"}],"timestamp":1735689601000}}
            {"type":"message","id":"assistant","parentId":"user","timestamp":"2025-01-01T00:00:02Z","message":{"role":"assistant","content":[{"type":"toolCall","id":"call-1","name":"read","arguments":{"path":"a.txt"}}],"provider":"openai","model":"gpt-4o","timestamp":1735689602000}}
            {"type":"message","id":"result","parentId":"assistant","timestamp":"2025-01-01T00:00:03Z","message":{"role":"toolResult","toolCallId":"call-1","toolName":"read","content":[{"type":"text","text":"contents"}],"isError":true,"timestamp":1735689603000}}
            """;

        var imported = PiJsonlSessionInterchange.Import(jsonl);
        Assert.IsType<DataContent>(imported.ActiveMessages()[0].Contents[1]);
        Assert.IsType<FunctionCallContent>(Assert.Single(imported.ActiveMessages()[1].Contents));
        var failure = Assert.IsType<FunctionResultContent>(Assert.Single(imported.ActiveMessages()[2].Contents));
        Assert.NotNull(failure.Exception);

        var serialized = PiJsonlSessionInterchange.Export(imported);
        Assert.Contains("\"mimeType\":\"image/png\"", serialized);
        Assert.Contains("\"toolCallId\":\"call-1\"", serialized);
        Assert.Contains("\"isError\":true", serialized);
    }

    [Fact]
    public async Task ExportFileIsPrivateDoesNotOverwriteAndRemovesCancelledPartialOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pi-jsonl-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = new ConversationSession(root, "fixture-model", "https://offline.example/v1", "fixture");
            session.Append(new ChatMessage(ChatRole.User, "interoperable"));
            var path = Path.Combine(root, "export.jsonl");
            await PiJsonlSessionInterchange.ExportToFileAsync(session, path);
            Assert.Contains("\"version\":3", await File.ReadAllTextAsync(path));
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            await Assert.ThrowsAsync<IOException>(() => PiJsonlSessionInterchange.ExportToFileAsync(session, path));
            Assert.Contains("interoperable", await File.ReadAllTextAsync(path));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var partialPath = Path.Combine(root, "cancelled.jsonl");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PiJsonlSessionInterchange.ExportToFileAsync(session, partialPath, cancelled.Token));
            Assert.False(File.Exists(partialPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ExportPreservesSelectedLeafAndSessionNameClears()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = $$$"""
            {"type":"session","version":3,"id":"name-session","timestamp":"2025-01-01T00:00:00Z","cwd":"{{{cwd}}}"}
            {"type":"message","id":"user","parentId":null,"timestamp":"2025-01-01T00:00:01Z","message":{"role":"user","content":"context","timestamp":1735689601000}}
            {"type":"session_info","id":"named","parentId":"user","timestamp":"2025-01-01T00:00:02Z","name":"old name"}
            """;
        var session = PiJsonlSessionInterchange.Import(input);
        Assert.Equal("old name", session.Name);

        session.Rename(null);
        var cleared = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(session));
        Assert.Null(cleared.Name);
        Assert.Equal("named", cleared.Tree.Entries[^1].ParentId);

        cleared.Rename("new name");
        var renamed = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(cleared));
        Assert.Equal("new name", renamed.Name);
        Assert.Equal(cleared.Tree.HeadId, renamed.Tree.Entries[^1].ParentId);
    }

    [Fact]
    public void ThinkingLevelChangesPersistAndRoundTripAsPiEntries()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture-model", null, "fixture");
        session.AppendThinkingLevelChange("max");

        Assert.Equal("max", PiJsonlSessionInterchange.GetThinkingLevel(session));
        var exported = PiJsonlSessionInterchange.Export(session);
        using var change = JsonDocument.Parse(exported.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("thinking_level_change", StringComparison.Ordinal)));
        Assert.Equal("max", change.RootElement.GetProperty("thinkingLevel").GetString());
        Assert.Equal("max", PiJsonlSessionInterchange.GetThinkingLevel(PiJsonlSessionInterchange.Import(exported)));
    }

    [Fact]
    public void MalformedLinesAreSkippedAndInvalidSessionHeadersFailClosed()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = V3Session(cwd).Replace("{\"type\":\"context_edit\"", "not json\n{\"type\":\"context_edit\"", StringComparison.Ordinal);
        Assert.Equal(10, PiJsonlSessionInterchange.Import(input).Tree.Entries.Count);
        Assert.Contains("does not begin with a Pi session header", Assert.Throws<InvalidDataException>(() =>
            PiJsonlSessionInterchange.Import("{\"type\":\"message\"}\n")).Message);
        Assert.Contains("does not begin with a Pi session header", Assert.Throws<InvalidDataException>(() =>
            PiJsonlSessionInterchange.Import("{\"type\":3}\n")).Message);
        Assert.Contains("version must be an integer", Assert.Throws<InvalidDataException>(() =>
            PiJsonlSessionInterchange.Import($"{{\"type\":\"session\",\"version\":\"3\",\"id\":\"x\",\"cwd\":\"{cwd}\"}}\n")).Message);
    }

    [Fact]
    public void MalformedLineKeepsFollowingOrphanInTheActiveHistoryAndPreservesItsPiParent()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = $$$"""
            {"type":"session","version":3,"id":"session-orphan","timestamp":"2026-09-27T10:00:00.000Z","cwd":"{{{cwd}}}"}
            {"type":"message","id":"entry-user","parentId":null,"timestamp":"2026-09-27T10:00:01.000Z","message":{"role":"user","content":"before corrupt row","timestamp":1780000000000}}
            not-json
            {"type":"message","id":"entry-assistant","parentId":"entry-missing","timestamp":"2026-09-27T10:00:03.000Z","message":{"role":"assistant","content":"survives as orphan","timestamp":1780000002000,"provider":"openai","model":"gpt-4o"}}
            """;

        var imported = PiJsonlSessionInterchange.Import(input);

        Assert.Equal("entry-assistant", imported.Tree.HeadId);
        Assert.Equal(["survives as orphan"], imported.ActiveMessages().Select(message => message.Text));
        var exported = PiJsonlSessionInterchange.Export(imported);
        using var orphan = JsonDocument.Parse(exported.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("entry-assistant", StringComparison.Ordinal)));
        Assert.Equal("entry-missing", orphan.RootElement.GetProperty("parentId").GetString());
        Assert.Equal(["survives as orphan"],
            PiJsonlSessionInterchange.Import(exported).ActiveMessages().Select(message => message.Text));
    }

    [Fact]
    public void ForwardParentEntryBuildsTheSameTreeShapeAndKeepsPhysicalEntryOrder()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = $$$"""
            {"type":"session","version":3,"id":"session-forward","timestamp":"2026-09-27T10:00:00.000Z","cwd":"{{{cwd}}}"}
            {"type":"message","id":"child-first","parentId":"parent-later","timestamp":"2026-09-27T10:00:01.000Z","message":{"role":"assistant","content":"child"}}
            {"type":"message","id":"parent-later","parentId":null,"timestamp":"2026-09-27T10:00:02.000Z","message":{"role":"user","content":"parent"}}
            """;

        var imported = PiJsonlSessionInterchange.Import(input);
        var entries = PiJsonlSessionInterchange.ProjectEntries(imported);
        var child = Assert.Single(imported.Tree.Entries, entry => entry.Id == "child-first");
        var parent = Assert.Single(imported.Tree.Entries, entry => entry.Id == "parent-later");

        Assert.Equal(["child-first", "parent-later"],
            entries.Select(entry => entry.GetProperty("id").GetString()));
        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal("parent-later", imported.Tree.HeadId);
        Assert.Equal(["parent-later"], imported.Tree.ActivePath().Select(entry => entry.Id));
        var restored = ConversationSession.Parse(imported.ToJson());
        Assert.Equal(["child-first", "parent-later"], restored.Tree.Entries.Select(entry => entry.Id));
        Assert.Equal(parent.Id, Assert.Single(restored.Tree.Entries, entry => entry.Id == child.Id).ParentId);
    }

    [Fact]
    public void SessionHeaderWithoutWorkingDirectoryUsesTheInvocationDirectory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-jsonl-default-cwd-" + Guid.NewGuid().ToString("N"));
        var input = """
            {"type":"session","version":3,"id":"session-no-cwd","timestamp":"2026-09-27T10:00:00.000Z"}
            {"type":"message","id":"entry-user","parentId":null,"timestamp":"2026-09-27T10:00:01.000Z","message":{"role":"user","content":"hello"}}
            """;

        var imported = PiJsonlSessionInterchange.Import(input, workingDirectoryFallback: cwd);

        Assert.Equal(Path.GetFullPath(cwd), imported.WorkingDirectory);
        Assert.Equal(["hello"], imported.ActiveMessages().Select(message => message.Text));
    }

    private static string V3Session(string cwd) => $$$"""
        {"type":"session","version":3,"id":"session-v3","timestamp":"2024-12-03T14:00:00.000Z","cwd":"{{{cwd}}}"}
        {"type":"message","id":"a-root","parentId":null,"timestamp":"2024-12-03T14:00:01.000Z","message":{"role":"system","content":"instructions","timestamp":1733234401000}}
        {"type":"message","id":"b-user","parentId":"a-root","timestamp":"2024-12-03T14:00:02.000Z","message":{"role":"user","content":"original","timestamp":1733234402000}}
        {"type":"message","id":"c-assistant","parentId":"b-user","timestamp":"2024-12-03T14:00:03.000Z","message":{"role":"assistant","content":[{"type":"text","text":"reply"}],"provider":"openai","model":"gpt-4o","timestamp":1733234403000}}
        {"type":"context_edit","id":"edit-a","parentId":"c-assistant","timestamp":"2024-12-03T14:00:04.000Z","targetId":"c-assistant","replacement":{"content":"edited"}}
        {"type":"compaction","id":"compact","parentId":"edit-a","timestamp":"2024-12-03T14:00:05.000Z","summary":"prior summary","firstKeptEntryId":"c-assistant","tokensBefore":400,"systemMessage":{"role":"system","content":"checkpoint","timestamp":1733234405000}}
        {"type":"message","id":"kept-next","parentId":"compact","timestamp":"2024-12-03T14:00:06.000Z","message":{"role":"user","content":"next","timestamp":1733234406000}}
        {"type":"branch_summary","id":"branch-summary","parentId":"b-user","timestamp":"2024-12-03T14:00:07.000Z","fromId":"kept-next","summary":"old path"}
        {"type":"context_edit","id":"edit-user","parentId":"branch-summary","timestamp":"2024-12-03T14:00:08.000Z","targetId":"b-user","replacement":{"content":"alternate"}}
        {"type":"model_change","id":"model-change","parentId":"edit-user","timestamp":"2024-12-03T14:00:09.000Z","provider":"anthropic","modelId":"claude-sonnet-4-5"}
        {"type":"message","id":"branch-user","parentId":"model-change","timestamp":"2024-12-03T14:00:10.000Z","message":{"role":"user","content":"branch prompt","timestamp":1733234410000}}
        """;
}
