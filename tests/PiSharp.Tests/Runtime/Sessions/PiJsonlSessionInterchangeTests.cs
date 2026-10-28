using System.Text;
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
    public void NativeReasoningSignatureSurvivesPiJsonlExportAndReimport()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture-model", null, "fixture");
        session.Append(new ChatMessage(ChatRole.User, "prompt"));
        session.Append(new ChatMessage(ChatRole.Assistant,
        [
            new TextReasoningContent("private reasoning") { ProtectedData = "opaque-reasoning-signature" },
            new TextContent("answer")
        ]));

        var nativeReloaded = ConversationSession.Parse(session.ToJson());
        var exported = PiJsonlSessionInterchange.Export(nativeReloaded);
        using var assistantEntry = JsonDocument.Parse(exported.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"role\":\"assistant\"", StringComparison.Ordinal)));
        var reasoning = assistantEntry.RootElement.GetProperty("message").GetProperty("content")
            .EnumerateArray().Single(item => item.GetProperty("type").GetString() == "thinking");

        Assert.Equal("private reasoning", reasoning.GetProperty("thinking").GetString());
        Assert.Equal("opaque-reasoning-signature", reasoning.GetProperty("thinkingSignature").GetString());

        var imported = PiJsonlSessionInterchange.Import(exported);
        var restoredReasoning = Assert.Single(Assert.Single(imported.ContextMessages(), message =>
            message.Role == ChatRole.Assistant).Contents.OfType<TextReasoningContent>());
        Assert.Equal("opaque-reasoning-signature", restoredReasoning.ProtectedData);
    }

    [Fact]
    public async Task AbortedAssistantFromPinnedPiSurvivesNativeReloadAndPiExport()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-interrupted-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixturePath = Path.Combine(FindRepositoryRoot(), "docs", "parity", "fixtures",
                "session-interrupted-assistant-b06-2026-10-24.jsonl");
            var imported = PiJsonlSessionInterchange.Import(await File.ReadAllTextAsync(fixturePath),
                workingDirectoryOverride: root);
            var store = new ConversationStore(root, Path.Combine(root, "sessions"));
            var path = store.NewPath(imported);
            await store.SaveAsync(imported, path);

            var reloaded = await store.LoadAsync(path);
            Assert.Equal([ChatRole.User, ChatRole.Assistant], reloaded.ContextMessages().Select(message => message.Role));
            Assert.Equal(["interrupted prompt", "partial output"], reloaded.ContextMessages().Select(message => message.Text));
            var reasoning = Assert.Single(reloaded.ContextMessages()[1].Contents.OfType<TextReasoningContent>());
            Assert.Equal("partial reasoning", reasoning.Text);
            Assert.Equal("partial-signature", reasoning.ProtectedData);

            var exported = PiJsonlSessionInterchange.Export(reloaded);
            using var assistant = JsonDocument.Parse(exported.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.Contains("\"id\":\"interrupted-assistant\"", StringComparison.Ordinal)));
            var message = assistant.RootElement.GetProperty("message");
            Assert.Equal("aborted", message.GetProperty("stopReason").GetString());
            Assert.Equal("Request was aborted", message.GetProperty("errorMessage").GetString());
            Assert.Equal("partial-signature", message.GetProperty("content").EnumerateArray()
                .Single(content => content.GetProperty("type").GetString() == "thinking")
                .GetProperty("thinkingSignature").GetString());

            var roundTripped = PiJsonlSessionInterchange.Import(exported, workingDirectoryOverride: root);
            Assert.Equal(reloaded.ContextMessages().Select(message => message.Role),
                roundTripped.ContextMessages().Select(message => message.Role));
            Assert.Equal(reloaded.ContextMessages().Select(message => message.Text),
                roundTripped.ContextMessages().Select(message => message.Text));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RichPinnedSessionFixtureRoundTripsThroughNativePersistenceAndPiJsonl()
    {
        var fixturePath = Path.Combine(FindRepositoryRoot(), "docs", "parity", "fixtures",
            "session-jsonl-rich-content-b06-2026-10-24.jsonl");
        var imported = PiJsonlSessionInterchange.Import(File.ReadAllText(fixturePath));
        AssertRichContentSession(imported);

        var nativeReloaded = ConversationSession.Parse(imported.ToJson());
        Assert.Equal(imported.Tree.Entries.Select(entry => entry.Id),
            nativeReloaded.Tree.Entries.Select(entry => entry.Id));
        Assert.Equal(imported.Tree.Entries.Select(entry => entry.ParentId),
            nativeReloaded.Tree.Entries.Select(entry => entry.ParentId));
        AssertRichContentSession(nativeReloaded);

        var exported = PiJsonlSessionInterchange.Export(nativeReloaded);
        var records = exported.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.Equal(imported.Tree.Entries.Select(entry => entry.Id),
            records.Skip(1).Select(entry => entry.GetProperty("id").GetString()));
        var extensionRecord = Assert.Single(records,
            entry => entry.GetProperty("id").GetString() == "rich-extension-state");
        Assert.Equal("fixture.session-state", extensionRecord.GetProperty("customType").GetString());
        Assert.Equal("must-not-enter-context", extensionRecord.GetProperty("data")
            .GetProperty("extensionOnly").GetString());
        var customMessageRecord = Assert.Single(records,
            entry => entry.GetProperty("id").GetString() == "rich-custom-message");
        Assert.Equal(7, customMessageRecord.GetProperty("details").GetProperty("revision").GetInt32());
        Assert.False(customMessageRecord.GetProperty("display").GetBoolean());

        if (Environment.GetEnvironmentVariable("PISHARP_SESSION_JSONL_ORACLE_EXPORT_PATH") is { Length: > 0 } exportPath)
            File.WriteAllText(exportPath, exported);

        var piSharpReloaded = PiJsonlSessionInterchange.Import(exported);
        Assert.Equal(imported.Tree.Entries.Select(entry => entry.Id),
            piSharpReloaded.Tree.Entries.Select(entry => entry.Id));
        Assert.Equal(imported.Tree.Entries.Select(entry => entry.ParentId),
            piSharpReloaded.Tree.Entries.Select(entry => entry.ParentId));
        AssertRichContentSession(piSharpReloaded);
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

        var messages = PiSessionRunProjection.ProjectRunMessages(session, runStart, "openai-completions");

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

        var projectedAssistant = Assert.Single(PiSessionRunProjection.ProjectRunMessages(
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

        var synthesizedAssistant = Assert.Single(PiSessionRunProjection.ProjectRunMessages(
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
    public void CompactionSummaryBoundaryAndUsageRoundTripThroughNativeAndPiJsonl()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture-model", null, "fixture");
        session.Append(new ChatMessage(ChatRole.User, "older question"));
        session.Append(new ChatMessage(ChatRole.Assistant, "older answer"));
        session.Append(new ChatMessage(ChatRole.User, "retained question"));
        session.Append(new ChatMessage(ChatRole.Assistant, "retained answer"));
        var plan = Assert.IsType<ConversationSession.CompactionPlan>(session.PrepareCompaction(1));
        var usage = UsageRecord.Create("fixture-model", "compaction", new UsageDetails
        {
            InputTokenCount = 12,
            OutputTokenCount = 12,
            CachedInputTokenCount = 2,
            TotalTokenCount = 24,
            AdditionalCounts = new() { ["cacheWriteTokens"] = 3 }
        }, new ModelPricing(Input: 2m, Output: 8m, CachedInput: 1m, CachedWrite: 3m));

        session.AppendCompaction(plan, "older history summary", 1, tokensBefore: 500, usage: usage);
        session.AppendUsage(usage);

        var compacted = Assert.Single(session.Tree.ActivePath(), entry => entry.Type == "compaction");
        Assert.Equal(plan.FirstKeptEntryId, compacted.Payload.GetProperty("firstKeptEntryId").GetString());
        Assert.Equal("older history summary", compacted.Payload.GetProperty("summary").GetString());
        Assert.Equal(500, compacted.Payload.GetProperty("tokensBefore").GetInt32());
        Assert.Equal(24, compacted.Payload.GetProperty("usage").GetProperty("totalTokens").GetInt64());
        Assert.Equal(24, Assert.Single(session.ActiveUsage(), record => record.Source == "compaction").TotalTokens);

        var exported = PiJsonlSessionInterchange.Export(session);
        using var compactEntry = JsonDocument.Parse(exported.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"type\":\"compaction\"", StringComparison.Ordinal)));
        Assert.Equal(plan.FirstKeptEntryId, compactEntry.RootElement.GetProperty("firstKeptEntryId").GetString());
        Assert.Equal(24, compactEntry.RootElement.GetProperty("usage").GetProperty("totalTokens").GetInt64());

        var nativeReloaded = ConversationSession.Parse(session.ToJson());
        Assert.Equal(24, Assert.Single(nativeReloaded.ActiveUsage(), record => record.Source == "compaction").TotalTokens);
        var imported = PiJsonlSessionInterchange.Import(exported);
        var importedCompaction = Assert.Single(imported.Tree.ActivePath(), entry => entry.Type == "compaction");
        Assert.Equal("older history summary", importedCompaction.Payload.GetProperty("summary").GetString());
        Assert.Equal(plan.FirstKeptEntryId, importedCompaction.Payload.GetProperty("firstKeptEntryId").GetString());
        Assert.Equal(24, importedCompaction.Payload.GetProperty("piOriginalEntry").GetProperty("usage")
            .GetProperty("totalTokens").GetInt64());
        Assert.Equal(24, Assert.Single(imported.ActiveUsage(), record => record.Source == "compaction").TotalTokens);
        Assert.Equal(session.ContextMessages().Select(message => message.Text),
            imported.ContextMessages().Select(message => message.Text));
    }

    [Fact]
    public void PinnedV3ContextFixtureRoundTripsItsSelectedBranchThroughNativeAndPiJsonl()
    {
        var fixturePath = Path.Combine(FindRepositoryRoot(), "docs", "parity", "fixtures", "session-v3-branch.jsonl");
        var input = File.ReadAllText(fixturePath);
        var imported = PiJsonlSessionInterchange.Import(input);
        var expectedText = new[]
        {
            "instructions",
            "alternate",
            "The following is a summary of a branch that this conversation came back from:\n\n<summary>\nabandoned path\n</summary>",
            "branch prompt"
        };

        Assert.Equal("12345678-1234-1234-1234-123456789abc", imported.Id);
        Assert.Equal("daaaaaaa", imported.Tree.HeadId);
        Assert.Equal(expectedText, imported.ContextMessages().Select(message => message.Text));
        Assert.Equal("high", PiJsonlSessionInterchange.GetThinkingLevel(imported));

        var nativeReloaded = ConversationSession.Parse(imported.ToJson());

        Assert.Equal(imported.Id, nativeReloaded.Id);
        Assert.Equal(imported.Tree.HeadId, nativeReloaded.Tree.HeadId);
        Assert.Equal(imported.Tree.Entries.Select(entry => entry.Id), nativeReloaded.Tree.Entries.Select(entry => entry.Id));
        Assert.Equal(expectedText, nativeReloaded.ContextMessages().Select(message => message.Text));
        Assert.Equal("high", PiJsonlSessionInterchange.GetThinkingLevel(nativeReloaded));

        var reloaded = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(nativeReloaded));

        Assert.Equal(imported.Id, reloaded.Id);
        Assert.Equal(imported.Tree.HeadId, reloaded.Tree.HeadId);
        Assert.Equal(imported.Tree.Entries.Select(entry => entry.Id), reloaded.Tree.Entries.Select(entry => entry.Id));
        Assert.Equal(expectedText, reloaded.ContextMessages().Select(message => message.Text));
        Assert.Equal("high", PiJsonlSessionInterchange.GetThinkingLevel(reloaded));
    }

    [Fact]
    public void ImportedAssistantUsageContributesToActiveSessionStatistics()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var jsonl = $$$$$"""
            {"type":"session","version":3,"id":"usage-session","timestamp":"2024-12-03T14:00:00.000Z","cwd":"{{{{{cwd}}}}}"}
            {"type":"message","id":"usage-user","parentId":null,"timestamp":"2024-12-03T14:00:01.000Z","message":{"role":"user","content":"prompt"}}
            {"type":"message","id":"usage-assistant","parentId":"usage-user","timestamp":"2024-12-03T14:00:02.000Z","message":{"role":"assistant","provider":"openai","model":"fixture-model","content":"reply","usage":{"input":10,"output":12,"cacheRead":2,"cacheWrite":3,"totalTokens":24,"cost":{"total":0.0002}}}}
            """;

        var imported = PiJsonlSessionInterchange.Import(jsonl);

        var usage = Assert.Single(imported.ActiveUsage());
        Assert.Equal("fixture-model", usage.Model);
        Assert.Equal("model", usage.Source);
        Assert.Equal(10, usage.InputTokens);
        Assert.Equal(12, usage.OutputTokens);
        Assert.Equal(2, usage.CachedInputTokens);
        Assert.Equal(3, usage.CachedWriteTokens);
        Assert.Equal(24, usage.TotalTokens);
        Assert.Equal(0.0002m, usage.Cost);

        var roundTripped = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(imported));
        Assert.Equal(usage, Assert.Single(roundTripped.ActiveUsage()));
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
        Assert.Equal("custom", PiJsonlSessionInterchange.ProjectEntries(migratedV1)[1]
            .GetProperty("message").GetProperty("role").GetString());
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
        Assert.Equal("custom", PiJsonlSessionInterchange.ProjectEntries(migratedV2)[1]
            .GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("legacy", migratedV2.ActiveMessages()[1].Text);
    }

    [Fact]
    public void LegacyV1MigrationIsStableAfterCanonicalExportAndReimport()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = $$$"""
            {"type":"session","id":"legacy-v1-stable","timestamp":"2024-12-03T14:00:00.000Z","cwd":"{{{cwd}}}"}
            {"type":"message","timestamp":"2024-12-03T14:00:01.000Z","message":{"role":"user","content":"kept boundary"}}
            {"type":"compaction","timestamp":"2024-12-03T14:00:02.000Z","summary":"summary","firstKeptEntryIndex":1,"tokensBefore":50}
            {"type":"message","timestamp":"2024-12-03T14:00:03.000Z","message":{"role":"hookMessage","content":"hook"}}
            """;
        var generated = new Queue<string>(["a1111111", "b2222222", "c3333333"]);

        var first = PiJsonlSessionImporter.Import(input, generateEntryId: () => generated.Dequeue());
        var exportedOnce = PiJsonlSessionInterchange.Export(first);
        var reopened = PiJsonlSessionInterchange.Import(exportedOnce);
        var exportedTwice = PiJsonlSessionInterchange.Export(reopened);
        var records = exportedOnce.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

        Assert.Equal(exportedOnce.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1),
            exportedTwice.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1));
        Assert.Equal(3, records[0].GetProperty("version").GetInt32());
        Assert.Equal(["a1111111", "b2222222", "c3333333"],
            records.Skip(1).Select(entry => entry.GetProperty("id").GetString()));
        Assert.Equal("a1111111", records[2].GetProperty("firstKeptEntryId").GetString());
        Assert.False(records[2].TryGetProperty("firstKeptEntryIndex", out _));
        Assert.Equal("custom", records[3].GetProperty("message").GetProperty("role").GetString());
    }

    [Fact]
    public void V3HookMessageRoleIsPreservedAndFutureVersionIsRejected()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var v3 = $$$"""
            {"type":"session","version":3,"id":"current-v3","timestamp":"2026-10-24T00:00:00Z","cwd":"{{{cwd}}}"}
            {"type":"message","id":"hook","parentId":null,"message":{"role":"hookMessage","content":"preserve"}}
            """;

        var imported = PiJsonlSessionInterchange.Import(v3);
        var exported = PiJsonlSessionInterchange.Export(imported);
        using var hook = JsonDocument.Parse(exported.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);

        Assert.Equal("hookMessage", hook.RootElement.GetProperty("message").GetProperty("role").GetString());
        Assert.Contains("not supported", Assert.Throws<InvalidDataException>(() =>
            PiJsonlSessionInterchange.Import($"{{\"type\":\"session\",\"version\":4,\"id\":\"future\",\"cwd\":\"{cwd}\"}}\n")).Message);
    }

    [Fact]
    public void LegacyV1MigrationRetriesGeneratedEntryIdCollisions()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        const string v1 = """
            {"type":"session","id":"legacy-collision","timestamp":"2024-12-03T14:00:00.000Z","cwd":"CWD"}
            {"type":"message","timestamp":"2024-12-03T14:00:01.000Z","message":{"role":"user","content":"first","timestamp":1733234401000}}
            {"type":"message","timestamp":"2024-12-03T14:00:02.000Z","message":{"role":"user","content":"second","timestamp":1733234402000}}
            """;
        var generated = new Queue<string>(["deadbeef", "deadbeef", "cafebabe"]);

        var imported = PiJsonlSessionImporter.Import(
            v1.Replace("CWD", cwd, StringComparison.Ordinal),
            generateEntryId: () => generated.Dequeue());

        Assert.Equal(["deadbeef", "cafebabe"], imported.Tree.Entries.Select(entry => entry.Id));
        Assert.Equal("deadbeef", imported.Tree.Entries[1].ParentId);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("99", false)]
    [InlineData("1.5", false)]
    [InlineData("\"1\"", true)]
    [InlineData("null", true)]
    [InlineData("false", true)]
    public void LegacyV1MigrationMatchesPinnedInvalidCompactionIndexHandling(string indexJson, bool preserved)
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = $$$"""
            {"type":"session","id":"invalid-compaction-index","cwd":"{{{cwd}}}"}
            {"type":"message","timestamp":"2026-10-24T00:00:01Z","message":{"role":"user","content":"kept"}}
            {"type":"compaction","timestamp":"2026-10-24T00:00:02Z","summary":"summary","firstKeptEntryId":"kept-entry","firstKeptEntryIndex":{{{indexJson}}}}
            """;

        var generated = new Queue<string>(["kept-entry", "compact-entry"]);
        var imported = PiJsonlSessionImporter.Import(input, generateEntryId: () => generated.Dequeue());
        var compaction = Assert.Single(imported.Tree.Entries, entry => entry.Type == "compaction");
        var exported = PiJsonlSessionInterchange.ProjectEntries(imported)
            .Single(entry => entry.GetProperty("type").GetString() == "compaction");

        Assert.Equal(preserved, exported.TryGetProperty("firstKeptEntryIndex", out var migratedIndex));
        if (preserved) Assert.Equal(indexJson, migratedIndex.GetRawText());
        Assert.Equal("kept-entry", exported.GetProperty("firstKeptEntryId").GetString());
        Assert.Equal("summary", compaction.Payload.GetProperty("summary").GetString());
        Assert.Contains("kept", imported.ContextMessages().Select(message => message.Text));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    public void LegacyV1MigrationTreatsIntegralJsonNumbersAsPinnedArrayIndices(string indexJson)
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = $$$"""
            {"type":"session","id":"integral-compaction-index","cwd":"{{{cwd}}}"}
            {"type":"message","timestamp":"2026-10-24T00:00:01Z","message":{"role":"user","content":"kept"}}
            {"type":"compaction","timestamp":"2026-10-24T00:00:02Z","summary":"summary","firstKeptEntryId":"invalid-original","firstKeptEntryIndex":{{{indexJson}}}}
            """;
        var generated = new Queue<string>(["kept-entry", "compact-entry"]);

        var imported = PiJsonlSessionImporter.Import(input, generateEntryId: () => generated.Dequeue());
        var compaction = PiJsonlSessionInterchange.ProjectEntries(imported)
            .Single(entry => entry.GetProperty("type").GetString() == "compaction");

        Assert.Equal("kept-entry", compaction.GetProperty("firstKeptEntryId").GetString());
        Assert.False(compaction.TryGetProperty("firstKeptEntryIndex", out _));
    }

    [Fact]
    public void LegacyV1MigrationPreservesMalformedTimestampTypeWithoutChangingTheBody()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = $$$"""
            {"type":"session","id":"legacy-malformed-field","cwd":"{{{cwd}}}"}
            {"type":"message","timestamp":42,"message":{"role":"user","content":"kept"},"unknown":{"v":1}}
            """;

        var imported = PiJsonlSessionImporter.Import(input, generateEntryId: () => "a1111111");
        var exported = PiJsonlSessionInterchange.ProjectEntries(imported);
        var message = Assert.Single(exported);

        Assert.Equal(42, message.GetProperty("timestamp").GetInt32());
        Assert.Equal(1, message.GetProperty("unknown").GetProperty("v").GetInt32());
        Assert.Equal("kept", imported.ContextMessages().Single().Text);
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
    public void InvalidImportedImageKeepsReadableContextAndOriginalContentForExport()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var jsonl = $$$"""
            {"type":"session","version":3,"id":"invalid-image-session","cwd":"{{{cwd}}}"}
            {"type":"message","id":"user","parentId":null,"timestamp":"2025-01-01T00:00:01Z","message":{"role":"user","content":[{"type":"text","text":"before"},{"type":"image","data":"not base64!","mimeType":"image/png","futureField":42},{"type":"text","text":"after"}]}}
            """;

        var session = PiJsonlSessionInterchange.Import(jsonl);
        var message = Assert.Single(session.ContextMessages());
        Assert.Equal(["before", "[invalid Pi image content omitted]", "after"],
            message.Contents.OfType<TextContent>().Select(part => part.Text));
        Assert.DoesNotContain(message.Contents, part => part is DataContent);

        var exported = PiJsonlSessionInterchange.Export(session);
        var entry = JsonDocument.Parse(exported.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
        using (entry)
        {
            var image = entry.RootElement.GetProperty("message").GetProperty("content")[1];
            Assert.Equal("not base64!", image.GetProperty("data").GetString());
            Assert.Equal(42, image.GetProperty("futureField").GetInt32());
        }
        Assert.Equal(message.Text, Assert.Single(PiJsonlSessionInterchange.Import(exported).ContextMessages()).Text);
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
    public void ExportRetriesGeneratedSessionInfoIdCollisions()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture-model", null);
        session.Append(new ChatMessage(ChatRole.User, "hello"));
        var existingId = session.Tree.HeadId!;
        session.Rename("unrecorded title");
        var generated = new Queue<string>([existingId, "cafebabe"]);

        var jsonl = PiJsonlSessionWriter.Export(session, generateEntryId: () => generated.Dequeue());
        var imported = PiJsonlSessionInterchange.Import(jsonl);

        Assert.Equal([existingId, "cafebabe"], imported.Tree.Entries.Select(entry => entry.Id));
        Assert.Equal("unrecorded title", imported.Name);
        Assert.Equal(existingId, imported.Tree.Entries[^1].ParentId);
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
    public void ThousandsOfJsonlEntriesAndMalformedRowsKeepTheLongActiveChain()
    {
        const int entryCount = 2048;
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = new StringBuilder();
        input.AppendLine(JsonSerializer.Serialize(new { type = "session", version = 3, id = "long-chain", cwd }));
        string? parentId = null;
        for (var index = 0; index < entryCount; index++)
        {
            input.AppendLine($"not-json-{index}");
            var id = $"entry-{index:D4}";
            input.AppendLine(JsonSerializer.Serialize(new
            {
                type = "message",
                id,
                parentId,
                message = new { role = index % 2 == 0 ? "user" : "assistant", content = $"message-{index:D4}", timestamp = index + 1 }
            }));
            parentId = id;
        }

        var imported = PiJsonlSessionInterchange.Import(input.ToString());

        Assert.Equal(entryCount, imported.Tree.Entries.Count);
        Assert.Equal(entryCount, imported.ActiveMessages().Count);
        Assert.Equal($"message-{entryCount - 1:D4}", imported.ActiveMessages()[^1].Text);
    }

    [Fact]
    public void LargeToolResultSurvivesNativeAndPiJsonlRoundTrip()
    {
        const string toolCallId = "call-large-result";
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var resultText = new string('r', 2 * 1024 * 1024);
        var input = string.Join('\n',
            JsonSerializer.Serialize(new { type = "session", version = 3, id = "large-tool-result", cwd }),
            JsonSerializer.Serialize(new
            {
                type = "message",
                id = "call",
                parentId = (string?)null,
                message = new
                {
                    role = "assistant",
                    content = new[]
                    {
                        new { type = "toolCall", id = toolCallId, name = "read", arguments = new { path = "large.txt" } }
                    }
                }
            }),
            JsonSerializer.Serialize(new
            {
                type = "message",
                id = "result",
                parentId = "call",
                message = new { role = "toolResult", toolCallId, toolName = "read", content = new[] { new { type = "text", text = resultText } }, isError = false }
            }),
            "");

        var imported = PiJsonlSessionInterchange.Import(input);
        var nativeReload = ConversationSession.Parse(imported.ToJson());
        var exported = PiJsonlSessionInterchange.Export(nativeReload);
        var resultLine = exported.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single(line =>
        {
            using var record = JsonDocument.Parse(line);
            return record.RootElement.TryGetProperty("message", out var message) &&
                message.TryGetProperty("role", out var role) && role.GetString() == "toolResult";
        });
        using var result = JsonDocument.Parse(resultLine);

        Assert.Equal(toolCallId, result.RootElement.GetProperty("message").GetProperty("toolCallId").GetString());
        Assert.Equal(resultText, result.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ValidNonHeaderJsonBeforeSessionHeaderDoesNotCreateAValidSession(string firstRecord)
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var jsonl = $"{firstRecord}\n{{\"type\":\"session\",\"version\":3,\"id\":\"later-header\",\"cwd\":\"{cwd}\"}}\n";

        Assert.Throws<InvalidDataException>(() => PiJsonlSessionInterchange.Import(jsonl));
    }

    [Fact]
    public void LineEndingsAndInterruptedJsonlTailsKeepValidRecordsWithoutChangingInput()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var header = "{\"type\":\"session\",\"version\":3,\"id\":\"line-recovery\",\"cwd\":\"" + cwd + "\"}";
        const string message = "{\"type\":\"message\",\"id\":\"valid-tail\",\"parentId\":null,\"message\":{\"role\":\"user\",\"content\":\"kept\",\"timestamp\":1}}";
        var inputs = new (string Jsonl, string[] Messages)[]
        {
            ("not json\n" + header, []),
            (header + "\r\n\r\nnot json\r\n" + message, ["kept"]),
            (header + "\n" + message + "\n{\"type\":\"message\"", ["kept"])
        };

        foreach (var (input, expectedMessages) in inputs)
        {
            var imported = PiJsonlSessionInterchange.Import(input, workingDirectoryFallback: cwd);
            Assert.Equal("line-recovery", imported.Id);
            Assert.Equal(expectedMessages, imported.ActiveMessages().Select(message => message.Text));
        }
    }

    [Fact]
    public void OversizedRecordIsSkippedWithoutHidingFollowingValidRecords()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var header = JsonSerializer.Serialize(new { type = "session", version = 3, id = "oversized-record", cwd });
        var oversized = JsonSerializer.Serialize(new
        {
            type = "message",
            id = "large",
            parentId = (string?)null,
            message = new { role = "user", content = new string('x', 16 * 1024 * 1024 + 1), timestamp = 1 }
        });
        var following = "{\"type\":\"message\",\"id\":\"small\",\"parentId\":\"large\",\"message\":{\"role\":\"user\",\"content\":\"kept\",\"timestamp\":2}}";

        var imported = PiJsonlSessionInterchange.Import($"{header}\n{oversized}\n{following}\n");

        Assert.Equal(["kept"], imported.ActiveMessages().Select(message => message.Text));
    }

    [Fact]
    public void RecordAtMaximumEntrySizeIsAccepted()
    {
        const int maximumEntryBytes = 16 * 1024 * 1024;
        var cwd = Path.GetFullPath(Path.GetTempPath());
        string MessageRecord(string content) => JsonSerializer.Serialize(new
        {
            type = "message",
            id = "exact-limit",
            parentId = (string?)null,
            message = new { role = "user", content, timestamp = 1 }
        });

        var payload = new string('x', maximumEntryBytes - Encoding.UTF8.GetByteCount(MessageRecord(string.Empty)));
        var entry = MessageRecord(payload);
        Assert.Equal(maximumEntryBytes, Encoding.UTF8.GetByteCount(entry));
        var header = JsonSerializer.Serialize(new { type = "session", version = 3, id = "entry-limit", cwd });

        var imported = PiJsonlSessionInterchange.Import($"{header}\n{entry}\n");

        Assert.Equal(payload.Length, Assert.Single(imported.ActiveMessages()).Text.Length);
    }

    [Fact]
    public void Utf8BomBeforeSessionHeaderIsRejected()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = "\uFEFF" + $"{{\"type\":\"session\",\"version\":3,\"id\":\"bom\",\"cwd\":\"{cwd}\"}}\n";

        Assert.Throws<InvalidDataException>(() => PiJsonlSessionInterchange.Import(input));
    }

    [Theory]
    [InlineData("{\"type\":\"session\",\"version\":3,\"cwd\":\"/tmp\"}")]
    [InlineData("{\"type\":\"session\",\"version\":3,\"id\":42,\"cwd\":\"/tmp\"}")]
    [InlineData("{\"type\":\"session\",\"version\":3,\"id\":\" \",\"cwd\":\"/tmp\"}")]
    public void InvalidSessionIdentityIsRejected(string header)
    {
        Assert.Throws<InvalidDataException>(() => PiJsonlSessionInterchange.Import(header));
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
        imported.Append(new ChatMessage(ChatRole.User, "continue orphan"));
        Assert.Equal("entry-assistant", imported.Tree.Entries[^1].ParentId);
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
    public void LastPhysicalBranchIsSelectedAndSubsequentAppendUsesItAsParent()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = $$$"""
            {"type":"session","version":3,"id":"session-selected-branch","timestamp":"2026-10-24T00:00:00Z","cwd":"{{{cwd}}}"}
            {"type":"message","id":"root","parentId":null,"message":{"role":"user","content":"root"}}
            {"type":"message","id":"old-branch","parentId":"root","message":{"role":"user","content":"old"}}
            {"type":"message","id":"selected-branch","parentId":"root","message":{"role":"user","content":"selected"}}
            """;

        var imported = PiJsonlSessionInterchange.Import(input);

        Assert.Equal(["root", "old-branch", "selected-branch"], imported.Tree.Entries.Select(entry => entry.Id));
        Assert.Equal(["root", "selected"], imported.ActiveMessages().Select(message => message.Text));
        imported.Append(new ChatMessage(ChatRole.User, "continued"));
        Assert.Equal("selected-branch", imported.Tree.Entries[^1].ParentId);
        Assert.Equal(["root", "selected", "continued"], imported.ActiveMessages().Select(message => message.Text));
    }

    [Fact]
    public void DuplicateEntryIdsAreRejectedDuringPiJsonlImport()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = $$$"""
            {"type":"session","version":3,"id":"session-duplicate","timestamp":"2026-10-24T00:00:00Z","cwd":"{{{cwd}}}"}
            {"type":"message","id":"duplicate","parentId":null,"message":{"role":"user","content":"first"}}
            {"type":"message","id":"duplicate","parentId":null,"message":{"role":"user","content":"last"}}
            """;

        Assert.Throws<InvalidDataException>(() => PiJsonlSessionInterchange.Import(input));
    }

    [Fact]
    public void ParentCyclesAreRejectedDuringPiJsonlImport()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath());
        var input = $$$"""
            {"type":"session","version":3,"id":"session-cycle","timestamp":"2026-10-24T00:00:00Z","cwd":"{{{cwd}}}"}
            {"type":"message","id":"cycle-a","parentId":"cycle-b","message":{"role":"user","content":"a"}}
            {"type":"message","id":"cycle-b","parentId":"cycle-a","message":{"role":"user","content":"b"}}
            """;

        Assert.Throws<InvalidDataException>(() => PiJsonlSessionInterchange.Import(input));
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

    private static void AssertRichContentSession(ConversationSession session)
    {
        Assert.Equal("rich-content-session", session.Id);
        Assert.Equal("fixture-gpt", session.Model);
        Assert.Equal("rich-assistant-final", session.Tree.HeadId);
        Assert.Equal("high", PiJsonlSessionInterchange.GetThinkingLevel(session));
        Assert.Equal(
            ["rich-user", "rich-thinking", "rich-assistant-call", "rich-tool-result", "rich-extension-state", "rich-custom-message", "rich-assistant-final"],
            session.Tree.ActivePath().Select(entry => entry.Id));

        var messages = session.ContextMessages();
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.Assistant],
            messages.Select(message => message.Role));

        var userImage = Assert.Single(messages[0].Contents.OfType<DataContent>());
        Assert.Equal("image/png", userImage.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, userImage.Data.ToArray());

        var reasoning = Assert.Single(messages[1].Contents.OfType<TextReasoningContent>());
        Assert.Equal("I will read the invoice details.", reasoning.Text);
        Assert.Equal("opaque-fixture-signature", reasoning.ProtectedData);
        var toolCall = Assert.Single(messages[1].Contents.OfType<FunctionCallContent>());
        Assert.Equal("call-rich-1", toolCall.CallId);
        Assert.Equal("lookup_invoice", toolCall.Name);
        var sourceArgument = toolCall.Arguments!["source"] is JsonElement sourceElement
            ? sourceElement.GetString()
            : Assert.IsType<string>(toolCall.Arguments["source"]);
        Assert.Equal("image", sourceArgument);

        var toolResult = Assert.Single(messages[2].Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-rich-1", toolResult.CallId);
        var toolParts = Assert.IsAssignableFrom<IEnumerable<AIContent>>(toolResult.Result);
        Assert.Equal("invoice number 42", Assert.IsType<TextContent>(toolParts.First()).Text);
        var toolImage = Assert.IsType<DataContent>(toolParts.ElementAt(1));
        Assert.Equal("image/jpeg", toolImage.MediaType);
        Assert.Equal(new byte[] { 255, 0, 1 }, toolImage.Data.ToArray());

        var customMessage = messages[3];
        Assert.Equal("Visible to the model as extension context.", customMessage.Text);
        Assert.Equal("fixture.session-note", Assert.IsType<string>(customMessage.AdditionalProperties!["pisharp.extension.customType"]));
        Assert.False(Assert.IsType<bool>(customMessage.AdditionalProperties["pisharp.extension.display"]));
        Assert.Equal(7, Assert.IsType<JsonElement>(customMessage.AdditionalProperties["pisharp.extension.details"])
            .GetProperty("revision").GetInt32());
        var customImage = Assert.Single(customMessage.Contents.OfType<DataContent>());
        Assert.Equal("image/jpeg", customImage.MediaType);
        Assert.Equal(new byte[] { 255, 0, 1 }, customImage.Data.ToArray());

        Assert.DoesNotContain(messages, message => message.Text.Contains("must-not-enter-context", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate PiSharp.slnx from the test output directory.");
    }
}
