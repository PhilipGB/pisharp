using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class RpcModeTests
{
    [Fact]
    public async Task AcceptsPromptThenEmitsEventsAndSupportsSubsequentStateQueries()
    {
        var channel = Channel.CreateUnbounded<string>();
        var reader = new CommandReader(channel.Reader);
        using var output = new LockedWriter();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())), session);
        var service = new RpcMode(reader, output, run);
        var serving = service.ServeAsync();
        channel.Writer.TryWrite("{\"id\":\"before\",\"type\":\"get_state\"}");
        channel.Writer.TryWrite("{\"id\":\"prompt-1\",\"type\":\"prompt\",\"message\":\"hello\"}");
        await WaitForAsync(output, "agent_settled");
        channel.Writer.TryWrite("{\"id\":\"after\",\"type\":\"get_messages\"}");
        channel.Writer.TryWrite("{\"id\":\"name\",\"type\":\"set_session_name\",\"name\":\"my feature\"}");
        channel.Writer.TryWrite("{\"id\":\"fork-messages\",\"type\":\"get_fork_messages\"}");
        channel.Writer.TryWrite("{\"id\":\"blank-name\",\"type\":\"set_session_name\",\"name\":\"  \"}");
        channel.Writer.TryWrite("{\"id\":\"entries\",\"type\":\"get_entries\"}");
        var firstEntryId = session.Tree.Entries[0].Id;
        channel.Writer.TryWrite($"{{\"id\":\"entries-after-cursor\",\"type\":\"get_entries\",\"since\":\"{firstEntryId}\"}}");
        channel.Writer.TryWrite("{\"id\":\"bad-cursor\",\"type\":\"get_entries\",\"since\":\"missing\"}");
        channel.Writer.TryWrite("{\"id\":\"tree\",\"type\":\"get_tree\"}");
        channel.Writer.TryWrite("{\"id\":\"text\",\"type\":\"get_last_assistant_text\"}");
        channel.Writer.TryWrite("{\"id\":\"unknown\",\"type\":\"unsupported\"}");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        var events = output.Lines().Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "prompt-1" && e.RootElement.GetProperty("success").GetBoolean());
            var promptResponse = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "prompt-1");
            Assert.Equal("started", promptResponse.RootElement.GetProperty("data").GetProperty("disposition").GetString());
            var responseIndex = Array.FindIndex(output.Lines(), line => line.Contains("\"id\":\"prompt-1\"", StringComparison.Ordinal));
            var start = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "agent_start");
            Assert.Equal(["type"], start.RootElement.EnumerateObject().Select(property => property.Name));
            var startIndex = Array.FindIndex(output.Lines(), line => line.Contains("\"type\":\"agent_start\"", StringComparison.Ordinal));
            Assert.True(responseIndex >= 0 && startIndex > responseIndex);
            var agentEnd = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "agent_end");
            Assert.Equal(["type", "messages", "willRetry"], agentEnd.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.False(agentEnd.RootElement.GetProperty("willRetry").GetBoolean());
            var runMessages = agentEnd.RootElement.GetProperty("messages");
            Assert.Equal(2, runMessages.GetArrayLength());
            Assert.Equal("user", runMessages[0].GetProperty("role").GetString());
            Assert.Equal("hello", runMessages[0].GetProperty("content").GetString());
            Assert.Equal("assistant", runMessages[1].GetProperty("role").GetString());
            Assert.Equal("reply", runMessages[1].GetProperty("content")[0].GetProperty("text").GetString());
            var settled = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "agent_settled");
            Assert.Equal(["type"], settled.RootElement.EnumerateObject().Select(property => property.Name));
            var settledIndex = Array.FindIndex(output.Lines(), line => line.Contains("\"type\":\"agent_settled\"", StringComparison.Ordinal));
            var endIndex = Array.FindIndex(output.Lines(), line => line.Contains("\"type\":\"agent_end\"", StringComparison.Ordinal));
            Assert.True(settledIndex > endIndex && endIndex > startIndex);
            Assert.DoesNotContain(output.Lines(), line => line.Contains("prompt_accepted", StringComparison.Ordinal));
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "unknown" && !e.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "after" &&
                e.RootElement.GetProperty("data").GetProperty("messages").GetArrayLength() == 2);
            var sessionInfo = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "session_info_changed");
            Assert.Equal(["type", "name"], sessionInfo.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("my feature", sessionInfo.RootElement.GetProperty("name").GetString());
            var lines = output.Lines();
            var sessionInfoIndex = Array.FindIndex(lines, line => line.Contains("session_info_changed", StringComparison.Ordinal));
            var nameResponseIndex = Array.FindIndex(lines, line => line.Contains("\"id\":\"name\"", StringComparison.Ordinal));
            Assert.True(sessionInfoIndex >= 0 && nameResponseIndex > sessionInfoIndex);
            Assert.Equal("session_info", session.Tree.Entries[^1].Type);
            Assert.Equal("my feature", session.Tree.Entries[^1].Payload.GetProperty("name").GetString());
            Assert.Equal(session.Tree.Entries[^2].Id, session.Tree.Entries[^1].ParentId);
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "bad-cursor" &&
                !e.RootElement.GetProperty("success").GetBoolean() &&
                e.RootElement.GetProperty("error").GetString() == "Entry not found: missing");
            Assert.Equal("my feature", session.Name);
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "blank-name" &&
                !e.RootElement.GetProperty("success").GetBoolean() &&
                e.RootElement.GetProperty("error").GetString() == "Session name cannot be empty");
            var forkMessages = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "fork-messages").RootElement
                .GetProperty("data").GetProperty("messages");
            Assert.Single(forkMessages.EnumerateArray());
            Assert.Equal("hello", forkMessages[0].GetProperty("text").GetString());
            var entriesResponse = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "entries" &&
                e.RootElement.GetProperty("data").GetProperty("entries").GetArrayLength() == 3);
            var entriesData = entriesResponse.RootElement.GetProperty("data");
            Assert.Equal(["entries", "leafId"], entriesData.EnumerateObject().Select(property => property.Name));
            Assert.False(entriesData.TryGetProperty("format", out _));
            Assert.Equal("message", entriesData.GetProperty("entries")[0].GetProperty("type").GetString());
            Assert.Equal(firstEntryId, entriesData.GetProperty("entries")[0].GetProperty("id").GetString());
            Assert.Equal("session_info", entriesData.GetProperty("entries")[2].GetProperty("type").GetString());
            Assert.Equal("my feature", entriesData.GetProperty("entries")[2].GetProperty("name").GetString());
            Assert.Equal(session.Tree.HeadId, entriesData.GetProperty("leafId").GetString());
            var cursorEntries = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "entries-after-cursor").RootElement.GetProperty("data");
            Assert.Equal(2, cursorEntries.GetProperty("entries").GetArrayLength());
            Assert.Equal(session.Tree.Entries[1].Id, cursorEntries.GetProperty("entries")[0].GetProperty("id").GetString());
            Assert.Equal(session.Tree.Entries[2].Id, cursorEntries.GetProperty("entries")[1].GetProperty("id").GetString());
            Assert.Equal(session.Tree.HeadId, cursorEntries.GetProperty("leafId").GetString());
            var treeResponse = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "tree" &&
                e.RootElement.GetProperty("data").GetProperty("tree").GetArrayLength() == 1);
            var treeData = treeResponse.RootElement.GetProperty("data");
            Assert.Equal(["tree", "leafId"], treeData.EnumerateObject().Select(property => property.Name));
            Assert.False(treeData.TryGetProperty("format", out _));
            var rootNode = Assert.Single(treeData.GetProperty("tree").EnumerateArray());
            Assert.Equal(["entry", "children"], rootNode.EnumerateObject().Select(property => property.Name));
            Assert.Equal(firstEntryId, rootNode.GetProperty("entry").GetProperty("id").GetString());
            Assert.Equal(1, rootNode.GetProperty("children").GetArrayLength());
            var assistantNode = rootNode.GetProperty("children")[0];
            Assert.Equal("session_info", assistantNode.GetProperty("children")[0]
                .GetProperty("entry").GetProperty("type").GetString());
            Assert.Equal("my feature", assistantNode.GetProperty("children")[0]
                .GetProperty("entry").GetProperty("name").GetString());
            Assert.Equal(session.Tree.HeadId, treeData.GetProperty("leafId").GetString());
            Assert.Contains(events, e => e.RootElement.GetProperty("type").GetString() == "response" &&
                e.RootElement.GetProperty("id").GetString() == "text" &&
                e.RootElement.GetProperty("data").GetProperty("text").GetString() == "reply");
            Assert.DoesNotContain(events, e => e.RootElement.GetProperty("type").GetString() == "session");
        }
        finally { foreach (var e in events) e.Dispose(); }
    }

    [Fact]
    public async Task SessionNameSaveFailureRollsBackTheEntryAndNameWithoutEmittingSuccess()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())), session);
        var service = new RpcMode(new CommandReader(channel.Reader), output, run,
            save: _ => Task.FromException(new IOException("disk full")));
        var serving = service.ServeAsync();
        channel.Writer.TryWrite("{\"id\":\"name\",\"type\":\"set_session_name\",\"name\":\"draft\"}");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        var events = output.Lines().Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var response = Assert.Single(events);
            Assert.Equal("name", response.RootElement.GetProperty("id").GetString());
            Assert.False(response.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("disk full", response.RootElement.GetProperty("error").GetString());
            Assert.Null(session.Name);
            Assert.Empty(session.Tree.Entries);
            Assert.Null(session.Tree.HeadId);
        }
        finally { foreach (var item in events) item.Dispose(); }
    }

    [Fact]
    public async Task NewSessionAbortsAnActiveRunBeforeReturningAndRebindsState()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-rpc-new-session-abort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var channel = Channel.CreateUnbounded<string>();
            var reader = new CommandReader(channel.Reader);
            using var output = new LockedWriter();
            var client = new PartialBlockingClient();
            var previous = new ConversationSession(cwd, "fixture", null);
            var previousRun = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), previous);
            var currentRun = previousRun;
            var service = new RpcMode(reader, output, previousRun, getCurrentRun: () => currentRun,
                newSession: async (parentSession, token) =>
                {
                    Assert.Equal("/sessions/parent.jsonl", parentSession);
                    var next = new ConversationSession(cwd, "fixture", null, parentSessionPath: parentSession);
                    currentRun = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(cwd)), next);
                    return false;
                });
            var serving = service.ServeAsync();
            channel.Writer.TryWrite("{\"id\":\"prompt\",\"type\":\"prompt\",\"message\":\"hold open\"}");
            await WaitForAsync(output, "\"type\":\"agent_start\"");
            channel.Writer.TryWrite("{\"id\":\"replace\",\"type\":\"new_session\",\"parentSession\":\"/sessions/parent.jsonl\"}");
            await WaitForAsync(output, "\"id\":\"replace\"");
            await WaitForAsync(output, "\"type\":\"agent_settled\"");
            channel.Writer.TryWrite("{\"id\":\"state\",\"type\":\"get_state\"}");
            await WaitForAsync(output, "\"id\":\"state\"");
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));

            var events = output.Lines().Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var endIndex = Array.FindIndex(events, item =>
                    item.RootElement.TryGetProperty("type", out var type) && type.GetString() == "agent_end");
                var settledIndex = Array.FindIndex(events, item =>
                    item.RootElement.TryGetProperty("type", out var type) && type.GetString() == "agent_settled");
                var newSessionIndex = Array.FindIndex(events, item =>
                    item.RootElement.TryGetProperty("id", out var id) && id.GetString() == "replace");
                Assert.True(endIndex >= 0 && settledIndex > endIndex && newSessionIndex > settledIndex);
                var finalMessages = events[endIndex].RootElement.GetProperty("messages");
                Assert.True(finalMessages.GetArrayLength() > 0);
                Assert.Equal("aborted", finalMessages[finalMessages.GetArrayLength() - 1]
                    .GetProperty("stopReason").GetString());
                Assert.DoesNotContain(events, item =>
                    item.RootElement.TryGetProperty("type", out var type) && type.GetString() == "error");
                var state = Assert.Single(events, item =>
                    item.RootElement.TryGetProperty("id", out var id) && id.GetString() == "state");
                Assert.NotEqual(previous.Id, state.RootElement.GetProperty("data").GetProperty("sessionId").GetString());
                Assert.Equal("/sessions/parent.jsonl", currentRun.Conversation.ParentSessionPath);
            }
            finally { foreach (var item in events) item.Dispose(); }
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task SwitchSessionAbortsAnActiveRunBeforeInvokingReplacement()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-rpc-switch-session-abort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var previous = new ConversationSession(cwd, "fixture", null);
            var previousRun = await ConversationRun.OpenAsync(new PiAgent(new PartialBlockingClient(), new CodingTools(cwd)), previous);
            var currentRun = previousRun;
            var service = new RpcMode(new CommandReader(channel.Reader), output, previousRun,
                getCurrentRun: () => currentRun,
                switchSession: async (path, token) =>
                {
                    Assert.Equal("/sessions/next.session.json", path);
                    Assert.Contains(output.Lines(), line => line.Contains("\"type\":\"agent_settled\"", StringComparison.Ordinal));
                    var next = new ConversationSession(cwd, "fixture", null);
                    currentRun = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(cwd)), next,
                        token);
                    return false;
                });
            var serving = service.ServeAsync();
            channel.Writer.TryWrite("{\"id\":\"prompt\",\"type\":\"prompt\",\"message\":\"hold open\"}");
            await WaitForAsync(output, "\"type\":\"agent_start\"");
            channel.Writer.TryWrite("{\"id\":\"switch\",\"type\":\"switch_session\",\"sessionPath\":\"/sessions/next.session.json\"}");
            await WaitForAsync(output, "\"id\":\"switch\"");
            await WaitForAsync(output, "\"type\":\"agent_settled\"");
            channel.Writer.TryWrite("{\"id\":\"state\",\"type\":\"get_state\"}");
            await WaitForAsync(output, "\"id\":\"state\"");
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));

            var events = output.Lines().Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var response = Assert.Single(events, item =>
                    item.RootElement.TryGetProperty("id", out var id) && id.GetString() == "switch");
                Assert.True(response.RootElement.GetProperty("success").GetBoolean());
                Assert.False(response.RootElement.GetProperty("data").GetProperty("cancelled").GetBoolean());
                var settledIndex = Array.FindIndex(events, item =>
                    item.RootElement.TryGetProperty("type", out var type) && type.GetString() == "agent_settled");
                var responseIndex = Array.FindIndex(events, item =>
                    item.RootElement.TryGetProperty("id", out var id) && id.GetString() == "switch");
                Assert.True(responseIndex > settledIndex);
                var state = Assert.Single(events, item =>
                    item.RootElement.TryGetProperty("id", out var id) && id.GetString() == "state");
                Assert.NotEqual(previous.Id, state.RootElement.GetProperty("data").GetProperty("sessionId").GetString());
                Assert.DoesNotContain(events, item =>
                    item.RootElement.TryGetProperty("type", out var type) && type.GetString() == "error");
            }
            finally { foreach (var item in events) item.Dispose(); }
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task PromptPreflightFailureReturnsOneCorrelatedResponse()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var service = new RpcMode(new CommandReader(channel.Reader), output, run,
            promptPreflight: () => "Provider 'fixture' is not authenticated.");
        var serving = service.ServeAsync();
        channel.Writer.TryWrite("{\"id\":\"preflight\",\"type\":\"prompt\",\"message\":\"hello\"}");
        await WaitForAsync(output, "\"id\":\"preflight\"");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        var lines = output.Lines();
        using var response = JsonDocument.Parse(Assert.Single(lines, line =>
            line.Contains("\"id\":\"preflight\"", StringComparison.Ordinal) && line.Contains("\"type\":\"response\"", StringComparison.Ordinal)));
        Assert.False(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Single(lines);
        Assert.Equal("prompt", response.RootElement.GetProperty("command").GetString());
        Assert.Equal("Provider 'fixture' is not authenticated.", response.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(lines, line => line.Contains("\"type\":\"error\"", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("prompt_accepted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetModelReplacesTheRuntimeForSubsequentRpcCommands()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var session = new ConversationSession(Path.GetTempPath(), "fixture-model", "http://old.test/v1", "fixture");
        var currentRun = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())), session);
        var service = new RpcMode(new CommandReader(channel.Reader), output, currentRun,
            discoverModels: (_, _) => Task.FromResult<IReadOnlyList<ModelDescriptor>>(
                [new ModelDescriptor("fixture-next", "fixture", 8192, "configured", Provider: "fixture")]),
            setModel: async (model, token) =>
            {
                session.SelectModel(model.Id, "http://new.test/v1", model.Provider);
                currentRun = await ConversationRun.OpenAsync(
                    new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())), session, token);
                return model;
            },
            getCurrentRun: () => currentRun);
        var serving = service.ServeAsync();
        channel.Writer.TryWrite("{\"id\":\"switch\",\"type\":\"set_model\",\"provider\":\"fixture\",\"modelId\":\"fixture-next\"}");
        channel.Writer.TryWrite("{\"id\":\"state\",\"type\":\"get_state\"}");
        await WaitForAsync(output, "\"id\":\"state\"");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        using var switched = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
            line.Contains("\"id\":\"switch\"", StringComparison.Ordinal)));
        Assert.True(switched.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("fixture-next", switched.RootElement.GetProperty("data").GetProperty("id").GetString());
        Assert.Equal("fixture", switched.RootElement.GetProperty("data").GetProperty("provider").GetString());
        using var state = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
            line.Contains("\"id\":\"state\"", StringComparison.Ordinal)));
        Assert.Equal("fixture-next", state.RootElement.GetProperty("data").GetProperty("model").GetString());
        Assert.Equal("http://new.test/v1", currentRun.Conversation.Endpoint);
        Assert.Single(session.Tree.Entries, entry => entry.Type == "model_change");
    }

    [Fact]
    public async Task CycleModelReturnsNullWhenOnlyOneModelIsAvailable()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var session = new ConversationSession(Path.GetTempPath(), "fixture-model", "http://fixture.test/v1", "fixture");
        var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())), session);
        var service = new RpcMode(new CommandReader(channel.Reader), output, run,
            discoverModels: (_, _) => Task.FromResult<IReadOnlyList<ModelDescriptor>>(
                [new ModelDescriptor("fixture-model", "fixture", 8192, "configured", Provider: "fixture")]));
        var serving = service.ServeAsync();
        channel.Writer.TryWrite("{\"id\":\"cycle\",\"type\":\"cycle_model\"}");
        await WaitForAsync(output, "\"id\":\"cycle\"");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        using var response = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
            line.Contains("\"id\":\"cycle\"", StringComparison.Ordinal)));
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task SetThinkingLevelIsAcceptedDuringAnActiveRunAndEmitsBeforeItsResponse()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var client = new OrderedRpcQueueClient();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()),
            reasoning: new ReasoningOptions { Effort = ReasoningEffort.Low, Output = ReasoningOutput.Full }),
            session, reasoningLevel: "low");
        var thinking = "low";
        var service = new RpcMode(new CommandReader(channel.Reader), output, run,
            getThinkingLevel: () => thinking,
            getAvailableThinkingLevels: () => ["off", "low", "medium", "high"],
            supportsThinking: () => true,
            setThinkingLevelDuringRun: (level, token) =>
            {
                token.ThrowIfCancellationRequested();
                var options = level == "high"
                    ? new ReasoningOptions { Effort = ReasoningEffort.High, Output = ReasoningOutput.Full }
                    : null;
                if (!run.SetThinkingLevelDuringRun(level, options))
                    throw new InvalidOperationException("The active run is already settling.");
                thinking = level;
                return Task.FromResult(level);
            });
        var serving = service.ServeAsync();
        channel.Writer.TryWrite("{\"id\":\"prompt\",\"type\":\"prompt\",\"message\":\"hello\"}");
        await client.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        channel.Writer.TryWrite("{\"id\":\"thinking\",\"type\":\"set_thinking_level\",\"level\":\"high\"}");
        await WaitForAsync(output, "thinking_level_changed");
        await WaitForAsync(output, "\"id\":\"thinking\"");
        var linesAtChange = output.Lines();
        var thinkingEventIndex = Array.FindIndex(linesAtChange, line => line.Contains("thinking_level_changed", StringComparison.Ordinal));
        var responseIndex = Array.FindIndex(linesAtChange, line => line.Contains("\"id\":\"thinking\"", StringComparison.Ordinal));
        Assert.True(thinkingEventIndex >= 0 && responseIndex > thinkingEventIndex);
        Assert.Equal("high", thinking);

        client.ReleaseFirstRequest.TrySetResult();
        await WaitForAsync(output, "agent_settled");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(session.Tree.Entries, entry => entry.Type == "thinking_level_change");
        Assert.Equal("high", session.Tree.Entries.Last().Payload.GetProperty("thinkingLevel").GetString());
    }

    [Fact]
    public async Task RuntimePreflightFailureReturnsOnlyCorrelatedErrorWithoutStartingAnAgentRun()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.Assistant, new string('x', 3000)));
        var run = await ConversationRun.OpenAsync(new PiAgent(new SummaryFailureClient(), new CodingTools(Path.GetTempPath())),
            session, autoCompaction: new AutoCompactionPolicy(1800, 300));
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
        channel.Writer.TryWrite("{\"id\":\"compact-failed\",\"type\":\"prompt\",\"message\":\"hello\"}");
        await WaitForAsync(output, "\"id\":\"compact-failed\"");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        var lines = output.Lines();
        var responseIndex = Array.FindIndex(lines, line => line.Contains("\"id\":\"compact-failed\"", StringComparison.Ordinal));
        Assert.True(responseIndex >= 0);
        Assert.Single(lines);
        using var response = JsonDocument.Parse(lines[responseIndex]);
        Assert.False(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("Estimated context still exceeds", response.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(lines, line => line.Contains("\"type\":\"error\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BashCommandStreamsCorrelatedOutputAndReturnsStructuredExitResult()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-bash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(root)),
                new ConversationSession(root, "fixture", null));
            var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();

            channel.Writer.TryWrite("{\"id\":\"bash-1\",\"type\":\"bash\",\"command\":\"printf stdout-marker; printf stderr-marker >&2; exit 7\"}");
            await WaitForAsync(output, "\"command\":\"bash\"");
            channel.Writer.TryWrite("{\"id\":\"after-bash\",\"type\":\"prompt\",\"message\":\"continue\"}");
            await WaitForAsync(output, "agent_settled");
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));

            var lines = output.Lines();
            using var response = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"command\":\"bash\"", StringComparison.Ordinal)));
            var result = response.RootElement.GetProperty("data");
            Assert.Equal("bash-1", response.RootElement.GetProperty("id").GetString());
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("stdout-marker", result.GetProperty("output").GetString());
            Assert.Contains("stderr-marker", result.GetProperty("output").GetString());
            Assert.Equal(7, result.GetProperty("exitCode").GetInt32());
            Assert.False(result.GetProperty("cancelled").GetBoolean());
            Assert.False(result.GetProperty("truncated").GetBoolean());
            Assert.False(result.TryGetProperty("fullOutputPath", out _));
            Assert.Contains(run.Conversation.ContextMessages(), message =>
                message.Text.Contains("Ran `printf stdout-marker; printf stderr-marker >&2; exit 7`", StringComparison.Ordinal));
            Assert.Contains(run.Conversation.ContextMessages(), message => message.Text.Contains("continue", StringComparison.Ordinal));
            var bashEntry = Assert.Single(run.Conversation.Tree.ActivePath(), entry => entry.Type == "bash_execution");
            Assert.False(bashEntry.Payload.GetProperty("excludeFromContext").GetBoolean());
            var restored = ConversationSession.Parse(run.Conversation.ToJson());
            Assert.False(Assert.Single(restored.Tree.ActivePath(), entry => entry.Type == "bash_execution")
                .Payload.GetProperty("excludeFromContext").GetBoolean());
            Assert.Contains(restored.ContextMessages(), message =>
                message.Text.Contains("Ran `printf stdout-marker; printf stderr-marker >&2; exit 7`", StringComparison.Ordinal));

            var updateIndices = lines.Select((line, index) => (line, index))
                .Where(item => item.line.Contains("bash_execution_update", StringComparison.Ordinal))
                .Select(item => item.index).ToArray();
            Assert.NotEmpty(updateIndices);
            var streamed = new System.Text.StringBuilder();
            foreach (var index in updateIndices)
            {
                using var update = JsonDocument.Parse(lines[index]);
                var eventData = update.RootElement.GetProperty("data");
                Assert.Equal("bash-1", eventData.GetProperty("OperationId").GetString());
                streamed.Append(eventData.GetProperty("Text").GetString());
            }
            Assert.Contains("stdout-marker", streamed.ToString());
            Assert.Contains("stderr-marker", streamed.ToString());
            var responseIndex = Array.FindIndex(lines, line => line.Contains("\"command\":\"bash\"", StringComparison.Ordinal));
            Assert.All(updateIndices, index => Assert.True(index < responseIndex));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UserBashHandlerCanStreamAndReplaceDirectBash()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-user-bash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var shellMarker = Path.Combine(root, "shell-ran");
        try
        {
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(root)),
                new ConversationSession(root, "fixture", null));
            var extensions = new ExtensionRegistration();
            var handlerCalls = 0;
            extensions.AddUserBashHandler(async (request, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                Assert.Equal($"touch {ProcessTestHelpers.ShellQuote(shellMarker)}", request.Command);
                Assert.True(request.ExcludeFromContext);
                Assert.Equal(root, request.WorkingDirectory);
                await request.EmitUpdateAsync("extension-first");
                await request.EmitUpdateAsync("extension-second");
                return new BashExecutionResult("extension-result", "extension-result", 23, false, true, null);
            });
            var serving = new RpcMode(new CommandReader(channel.Reader), output, run, extensions: extensions).ServeAsync();
            var command = $"touch {ProcessTestHelpers.ShellQuote(shellMarker)}";
            channel.Writer.TryWrite(JsonSerializer.Serialize(new
            {
                id = "user-bash",
                type = "bash",
                command,
                excludeFromContext = true
            }));
            await WaitForAsync(output, "\"id\":\"user-bash\",\"type\":\"response\",\"command\":\"bash\"");
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, handlerCalls);
            Assert.False(File.Exists(shellMarker));
            var lines = output.Lines();
            using var response = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"user-bash\"", StringComparison.Ordinal) &&
                line.Contains("\"command\":\"bash\"", StringComparison.Ordinal)));
            var data = response.RootElement.GetProperty("data");
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("extension-result", data.GetProperty("output").GetString());
            Assert.Equal(23, data.GetProperty("exitCode").GetInt32());
            Assert.True(data.GetProperty("truncated").GetBoolean());
            var updateTexts = lines.Where(line => line.Contains("bash_execution_update", StringComparison.Ordinal))
                .Select(line => JsonDocument.Parse(line))
                .Select(document =>
                {
                    using (document)
                    {
                        Assert.Equal("user-bash", document.RootElement.GetProperty("data").GetProperty("OperationId").GetString());
                        return document.RootElement.GetProperty("data").GetProperty("Text").GetString()!;
                    }
                }).ToArray();
            Assert.Equal(["extension-first", "extension-second"], updateTexts);
            var entry = Assert.Single(run.Conversation.Tree.ActivePath(), item => item.Type == "bash_execution");
            Assert.Equal("extension-result", entry.Payload.GetProperty("output").GetString());
            Assert.True(entry.Payload.GetProperty("excludeFromContext").GetBoolean());
            Assert.Empty(run.Conversation.ContextMessages());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FailedAndDeclinedUserBashHandlersFallThroughToTheShell()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-user-bash-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(root)),
                new ConversationSession(root, "fixture", null));
            var extensions = new ExtensionRegistration();
            extensions.AddUserBashHandler((_, _) =>
                Task.FromException<BashExecutionResult?>(new InvalidOperationException("extension failed")));
            extensions.AddUserBashHandler((_, _) => Task.FromResult<BashExecutionResult?>(null));
            var serving = new RpcMode(new CommandReader(channel.Reader), output, run, extensions: extensions).ServeAsync();
            channel.Writer.TryWrite("{\"id\":\"user-bash-fallback\",\"type\":\"bash\",\"command\":\"printf shell-fallback\"}");
            await WaitForAsync(output, "\"id\":\"user-bash-fallback\",\"type\":\"response\",\"command\":\"bash\"");
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));

            var lines = output.Lines();
            using var error = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("extension_error", StringComparison.Ordinal)));
            Assert.Equal("user-bash-fallback", error.RootElement.GetProperty("data").GetProperty("OperationId").GetString());
            Assert.Equal("extension failed", error.RootElement.GetProperty("data").GetProperty("Error").GetString());
            using var response = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"user-bash-fallback\"", StringComparison.Ordinal) &&
                line.Contains("\"command\":\"bash\"", StringComparison.Ordinal)));
            Assert.Equal("shell-fallback", response.RootElement.GetProperty("data").GetProperty("output").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AbortBashCancelsAnActiveUserBashHandler()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-user-bash-abort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var shellMarker = Path.Combine(root, "shell-ran");
        try
        {
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(root)),
                new ConversationSession(root, "fixture", null));
            var enteredHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var extensions = new ExtensionRegistration();
            extensions.AddUserBashHandler(async (_, cancellationToken) =>
            {
                enteredHandler.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            });
            var serving = new RpcMode(new CommandReader(channel.Reader), output, run, extensions: extensions).ServeAsync();
            channel.Writer.TryWrite(JsonSerializer.Serialize(new
            {
                id = "user-bash-abort",
                type = "bash",
                command = $"touch {ProcessTestHelpers.ShellQuote(shellMarker)}"
            }));
            await enteredHandler.Task.WaitAsync(TimeSpan.FromSeconds(5));
            channel.Writer.TryWrite("{\"id\":\"abort-user-bash\",\"type\":\"abort_bash\"}");
            await WaitForAsync(output, "\"command\":\"abort_bash\"");
            await WaitForAsync(output, "\"id\":\"user-bash-abort\",\"type\":\"response\",\"command\":\"bash\"");
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(File.Exists(shellMarker));
            using var response = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
                line.Contains("\"id\":\"user-bash-abort\"", StringComparison.Ordinal) &&
                line.Contains("\"command\":\"bash\"", StringComparison.Ordinal)));
            var data = response.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("cancelled").GetBoolean());
            Assert.False(data.TryGetProperty("exitCode", out _));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BashCommandCanPersistResultWithoutAddingItToModelContext()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-bash-excluded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var session = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(root)), session);
            var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();

            channel.Writer.TryWrite("{\"id\":\"bash-excluded\",\"type\":\"bash\",\"command\":\"printf excluded-marker\",\"excludeFromContext\":true}");
            await WaitForAsync(output, "\"command\":\"bash\"");
            using (var response = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
                       line.Contains("\"id\":\"bash-excluded\"", StringComparison.Ordinal))))
            {
                Assert.True(response.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal("excluded-marker", response.RootElement.GetProperty("data").GetProperty("output").GetString());
            }

            Assert.Empty(session.ContextMessages());
            var bashEntry = Assert.Single(session.Tree.ActivePath(), entry => entry.Type == "bash_execution");
            Assert.True(bashEntry.Payload.GetProperty("excludeFromContext").GetBoolean());
            var restored = ConversationSession.Parse(session.ToJson());
            Assert.Empty(restored.ContextMessages());
            var restoredBashEntry = Assert.Single(restored.Tree.ActivePath(), entry => entry.Type == "bash_execution");
            Assert.True(restoredBashEntry.Payload.GetProperty("excludeFromContext").GetBoolean());
            Assert.Equal("excluded-marker", restoredBashEntry.Payload.GetProperty("output").GetString());

            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));

            var restoredRun = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(root)), restored);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in restoredRun.RunEventsAsync("continue")) events.Add(item);
            Assert.Equal("agent_settled", events[^1].Type);
            Assert.DoesNotContain(restored.ContextMessages(), message =>
                message.Text.Contains("excluded-marker", StringComparison.Ordinal));
            Assert.Contains(restored.ContextMessages(), message => message.Text.Contains("continue", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AbortBashCancelsTheActiveRpcCommandAndReturnsCancelledResult()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-abort-bash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var started = Path.Combine(root, "started");
        var released = Path.Combine(root, "released");
        var finished = Path.Combine(root, "finished");
        try
        {
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(root)),
                new ConversationSession(root, "fixture", null));
            var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
            var command = $"touch {ProcessTestHelpers.ShellQuote(started)}; while [ ! -e {ProcessTestHelpers.ShellQuote(released)} ]; do :; done; touch {ProcessTestHelpers.ShellQuote(finished)}";
            channel.Writer.TryWrite(JsonSerializer.Serialize(new { id = "bash-running", type = "bash", command }));
            await ProcessTestHelpers.WaitForFileAsync(started);
            channel.Writer.TryWrite("{\"id\":\"abort-1\",\"type\":\"abort_bash\"}");
            await WaitForAsync(output, "\"command\":\"abort_bash\"");
            await WaitForAsync(output, "\"id\":\"bash-running\"");
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));

            using var abort = JsonDocument.Parse(Assert.Single(output.Lines(), line => line.Contains("\"command\":\"abort_bash\"", StringComparison.Ordinal)));
            Assert.True(abort.RootElement.GetProperty("success").GetBoolean());
            using var response = JsonDocument.Parse(Assert.Single(output.Lines(), line => line.Contains("\"command\":\"bash\"", StringComparison.Ordinal)));
            var result = response.RootElement.GetProperty("data");
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            Assert.True(result.GetProperty("cancelled").GetBoolean());
            Assert.False(result.TryGetProperty("exitCode", out _));
            Assert.False(File.Exists(finished));
            Assert.Contains("(command cancelled)", Assert.Single(run.Conversation.ActiveMessages()).Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BashCommandReturnsTailAndPrivateFullOutputMetadataWhenTruncated()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-bash-truncated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? fullOutputPath = null;
        try
        {
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(root)),
                new ConversationSession(root, "fixture", null));
            var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
            channel.Writer.TryWrite("{\"id\":\"bash-truncated\",\"type\":\"bash\",\"command\":\"seq 1 3000\"}");
            await WaitForAsync(output, "\"command\":\"bash\"");
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));

            using var response = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
                line.Contains("\"id\":\"bash-truncated\"", StringComparison.Ordinal) && line.Contains("\"command\":\"bash\"", StringComparison.Ordinal)));
            var result = response.RootElement.GetProperty("data");
            Assert.True(result.GetProperty("truncated").GetBoolean());
            var text = result.GetProperty("output").GetString()!;
            Assert.StartsWith("1001\n1002\n", text);
            Assert.EndsWith("2999\n3000", text);
            Assert.DoesNotContain("Full output:", text);
            fullOutputPath = result.GetProperty("fullOutputPath").GetString();
            Assert.NotNull(fullOutputPath);
            Assert.Contains("1\n2\n", await File.ReadAllTextAsync(fullOutputPath));
            Assert.Contains("2999\n3000", await File.ReadAllTextAsync(fullOutputPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(fullOutputPath) & (UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }
        finally
        {
            if (fullOutputPath is not null && File.Exists(fullOutputPath)) File.Delete(fullOutputPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PromptReceivedDuringStreamingQueuesAnotherTurnBeforeSettlement()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var client = new QueuedClient();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())), session);
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();

        channel.Writer.TryWrite("{\"id\":\"first\",\"type\":\"prompt\",\"message\":\"one\"}");
        await client.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        channel.Writer.TryWrite("{\"id\":\"missing-mode\",\"type\":\"prompt\",\"message\":\"two\"}");
        await WaitForAsync(output, "\"id\":\"missing-mode\"");
        using (var rejected = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
            line.Contains("\"id\":\"missing-mode\"", StringComparison.Ordinal))))
        {
            Assert.False(rejected.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("streamingBehavior", rejected.RootElement.GetProperty("error").GetString());
        }
        channel.Writer.TryWrite("{\"id\":\"second\",\"type\":\"prompt\",\"message\":\"two\",\"streamingBehavior\":\"followUp\"}");
        await WaitForAsync(output, "\"id\":\"second\"");
        client.ReleaseFirstRequest.TrySetResult();
        await WaitForAsync(output, "agent_settled");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        var responses = output.Lines().Where(line => line.Contains("\"type\":\"response\"", StringComparison.Ordinal))
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(3, responses.Length);
            var started = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "first");
            var queued = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "second");
            var missingBehavior = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "missing-mode");
            Assert.True(started.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("started", started.RootElement.GetProperty("data").GetProperty("disposition").GetString());
            Assert.True(queued.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("queued", queued.RootElement.GetProperty("data").GetProperty("disposition").GetString());
            Assert.False(missingBehavior.RootElement.GetProperty("success").GetBoolean());
            var lines = output.Lines();
            var agentStartIndices = lines.Select((line, index) => (line, index))
                .Where(item => item.line.Contains("\"type\":\"agent_start\"", StringComparison.Ordinal))
                .Select(item => item.index).ToArray();
            var agentEndIndices = lines.Select((line, index) => (line, index))
                .Where(item => item.line.Contains("\"type\":\"agent_end\"", StringComparison.Ordinal))
                .Select(item => item.index).ToArray();
            var agentSettledIndex = Array.FindIndex(lines, line => line.Contains("\"type\":\"agent_settled\"", StringComparison.Ordinal));
            Assert.Equal(2, agentStartIndices.Length);
            Assert.Equal(2, agentEndIndices.Length);
            Assert.True(agentStartIndices[0] >= 0 && agentEndIndices[0] > agentStartIndices[0] &&
                agentStartIndices[1] > agentEndIndices[0] && agentEndIndices[1] > agentStartIndices[1] &&
                agentSettledIndex > agentEndIndices[1]);
            using (var firstEnd = JsonDocument.Parse(lines[agentEndIndices[0]]))
            {
                Assert.False(firstEnd.RootElement.GetProperty("willRetry").GetBoolean());
                Assert.Equal(["one", "first reply"], firstEnd.RootElement.GetProperty("messages").EnumerateArray()
                    .Select(message => message.GetProperty("role").GetString() == "assistant"
                        ? message.GetProperty("content")[0].GetProperty("text").GetString()
                        : message.GetProperty("content").GetString()));
            }
            using (var secondEnd = JsonDocument.Parse(lines[agentEndIndices[1]]))
            {
                Assert.False(secondEnd.RootElement.GetProperty("willRetry").GetBoolean());
                Assert.Equal(["two", "second reply"], secondEnd.RootElement.GetProperty("messages").EnumerateArray()
                    .Select(message => message.GetProperty("role").GetString() == "assistant"
                        ? message.GetProperty("content")[0].GetProperty("text").GetString()
                        : message.GetProperty("content").GetString()));
            }
            Assert.Contains(lines, line => line.Contains("\"type\":\"queue_update\"", StringComparison.Ordinal) &&
                line.Contains("\"followUp\":[\"two\"]", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("prompt_queued", StringComparison.Ordinal));
            Assert.Equal(2, client.Requests);
            Assert.True(client.SecondRequestSawFirstTurn);
            Assert.Equal(2, session.ActiveMessages().Count(message => message.Role == ChatRole.User));
            Assert.Equal(1, output.Lines().Count(line => line.Contains("agent_settled", StringComparison.Ordinal)));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task RpcSteeringFollowUpAndClearQueueExposeDistinctPendingInput()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var client = new OrderedRpcQueueClient();
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();

        channel.Writer.TryWrite("{\"id\":\"start\",\"type\":\"prompt\",\"message\":\"initial\"}");
        await client.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        channel.Writer.TryWrite("{\"id\":\"f0\",\"type\":\"follow_up\",\"message\":\"discard follow\"}");
        channel.Writer.TryWrite("{\"id\":\"s0\",\"type\":\"steer\",\"message\":\"discard steer\"}");
        channel.Writer.TryWrite("{\"id\":\"clear\",\"type\":\"clear_queue\"}");
        await WaitForAsync(output, "\"command\":\"clear_queue\"");
        channel.Writer.TryWrite("{\"id\":\"follow\",\"type\":\"follow_up\",\"message\":\"later\"}");
        channel.Writer.TryWrite("{\"id\":\"steer\",\"type\":\"steer\",\"message\":\"direction\"}");
        await WaitForAsync(output, "\"id\":\"steer\"");
        client.ReleaseFirstRequest.TrySetResult();
        await WaitForAsync(output, "agent_settled");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["initial", "direction", "later"], client.LatestUserByRequest);
        using var clear = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
            line.Contains("\"command\":\"clear_queue\"", StringComparison.Ordinal)));
        Assert.Equal(["discard steer"], clear.RootElement.GetProperty("data").GetProperty("steering")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["discard follow"], clear.RootElement.GetProperty("data").GetProperty("followUp")
            .EnumerateArray().Select(item => item.GetString()));
        var queueUpdates = output.Lines().Where(line => line.Contains("\"type\":\"queue_update\"", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(queueUpdates);
        Assert.All(queueUpdates, line => Assert.DoesNotContain("\"format\":\"pisharp\"", line, StringComparison.Ordinal));
        using var snapshot = JsonDocument.Parse(Assert.Single(queueUpdates, line =>
            line.Contains("\"steering\":[\"discard steer\"]", StringComparison.Ordinal) &&
            line.Contains("\"followUp\":[\"discard follow\"]", StringComparison.Ordinal)));
        Assert.Equal(["type", "steering", "followUp"], snapshot.RootElement.EnumerateObject().Select(property => property.Name));
        foreach (var (id, command) in new[] { ("follow", "follow_up"), ("steer", "steer") })
        {
            using var response = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
                line.Contains($"\"id\":\"{id}\"", StringComparison.Ordinal) &&
                line.Contains($"\"command\":\"{command}\"", StringComparison.Ordinal)));
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("queued", response.RootElement.GetProperty("data").GetProperty("disposition").GetString());
        }
    }

    [Fact]
    public async Task RpcHtmlExportIsPrivateIncludesBranchesAndNeverOverwrites()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-rpc-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var session = new ConversationSession(dir, "fixture", null);
            session.Append(new ChatMessage(ChatRole.User, "first"));
            var root = session.Tree.HeadId;
            session.Append(new ChatMessage(ChatRole.Assistant, "inactive <script>"));
            session.Tree.Select(root);
            session.Append(new ChatMessage(ChatRole.Assistant, "active"));
            var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(dir)), session);
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
            channel.Writer.TryWrite("{\"id\":1,\"type\":\"export_html\"}");
            channel.Writer.TryWrite("{\"id\":2,\"type\":\"export_html\"}");
            channel.Writer.TryWrite("{\"id\":3,\"type\":\"export_html\",\"outputPath\":null}");
            var customPath = Path.Combine(dir, "custom.html");
            channel.Writer.TryWrite(JsonSerializer.Serialize(new { id = 4, type = "export_html", outputPath = customPath }));
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
            var path = Path.Combine(dir, $"pisharp-{session.Id[..12]}.html");
            var html = await File.ReadAllTextAsync(path);
            Assert.Contains("inactive", html);
            Assert.DoesNotContain("<script>", html);
            Assert.Contains("active", html);
            using var first = JsonDocument.Parse(output.Lines()[0]);
            Assert.True(first.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(path, first.RootElement.GetProperty("data").GetProperty("path").GetString());
            Assert.Equal(4, output.Lines().Length);
            foreach (var line in output.Lines().Skip(1).Take(2))
            {
                using var response = JsonDocument.Parse(line);
                Assert.False(response.RootElement.GetProperty("success").GetBoolean());
            }
            using var custom = JsonDocument.Parse(output.Lines()[3]);
            Assert.True(custom.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(customPath, custom.RootElement.GetProperty("data").GetProperty("path").GetString());
            Assert.True(File.Exists(customPath));
            if (OperatingSystem.IsLinux()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path) & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task SessionStatisticsCountActiveBranchAndExposeUnknownBillingThroughRpc()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "question"));
        var root = session.Tree.HeadId;
        session.Append(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("a", "read",
            new Dictionary<string, object?> { ["path"] = "x" })]));
        session.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("a", "contents")]));
        session.Tree.Select(root);
        session.Append(new ChatMessage(ChatRole.Assistant, "alternate"));
        var stats = SessionStatistics.Calculate(session);
        Assert.Equal(4, stats.Entries);
        Assert.Equal(2, stats.Leaves);
        Assert.Equal(2, stats.ActiveMessages);
        Assert.Equal(1, stats.UserTurns);
        Assert.Equal(0, stats.ToolCalls);
        Assert.Null(stats.BilledTokens);
        Assert.Null(stats.Cost);
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())), session);
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
        channel.Writer.TryWrite("{\"id\":\"stats\",\"type\":\"get_session_stats\"}");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        using var response = JsonDocument.Parse(Assert.Single(output.Lines()));
        var data = response.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetProperty("Leaves").GetInt32());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("BilledTokens").ValueKind);
    }

    [Fact]
    public async Task CommandsAndTemplateExpansionUseSharedResourceCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-resources-" + Guid.NewGuid().ToString("N"));
        var prompts = Path.Combine(root, "agent", "prompts");
        Directory.CreateDirectory(prompts);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(prompts, "review.md"), "Check $1");
            var resources = await PiSharp.Runtime.Resources.ResourceCatalog.LoadAsync(root, Path.Combine(root, "agent"), false);
            var channel = Channel.CreateUnbounded<string>();
            using var output = new LockedWriter();
            var session = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(root)), session);
            var service = new RpcMode(new CommandReader(channel.Reader), output, run, resources: resources);
            var serving = service.ServeAsync();
            channel.Writer.TryWrite("{\"id\":1,\"type\":\"get_commands\"}");
            channel.Writer.TryWrite("{\"id\":2,\"type\":\"prompt\",\"message\":\"/review concurrency\"}");
            await WaitForAsync(output, "agent_settled");
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Check concurrency", session.ActiveMessages().First().Text);
            Assert.Contains(output.Lines(), line => line.Contains("\"name\":\"review\"", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ModelDiscoveryIsAvailableThroughRpcWithoutModelCall()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run,
            discoverModels: (_, _) => Task.FromResult<IReadOnlyList<PiSharp.Runtime.Providers.ModelDescriptor>>(
                [new("model-one", "fixture", 4096, "loaded")])).ServeAsync();
        channel.Writer.TryWrite("{\"id\":5,\"type\":\"get_available_models\"}");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(output.Lines(), line => line.Contains("\"command\":\"get_available_models\"", StringComparison.Ordinal) &&
            line.Contains("\"Id\":\"model-one\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompactCommandRebuildsContextButNotRawMessages()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "one"));
        session.Append(new ChatMessage(ChatRole.Assistant, "answer"));
        session.Append(new ChatMessage(ChatRole.User, "two"));
        var run = await ConversationRun.OpenAsync(new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath())), session);
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
        channel.Writer.TryWrite("{\"id\":9,\"type\":\"compact\",\"instructions\":\"retain decisions\"}");
        await WaitForAsync(output, "\"compacted\":true");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, session.ActiveMessages().Count);
        Assert.Equal(2, session.ContextMessages().Count);
        Assert.Contains(output.Lines(), line => line.Contains("\"command\":\"compact\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbortSettlesAndPreservesInterruptionMarker()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(new BlockingClient(), new CodingTools(Path.GetTempPath())), session);
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
        channel.Writer.TryWrite("{\"id\":1,\"type\":\"prompt\",\"message\":\"wait\"}");
        await WaitForAsync(output, "model_request_started");
        channel.Writer.TryWrite("{\"id\":2,\"type\":\"abort\"}");
        await WaitForAsync(output, "agent_settled");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(session.Tree.Entries, entry => entry.Type == "interrupted");
        var lines = output.Lines();
        var agentEndIndex = Array.FindIndex(lines, line => line.Contains("\"type\":\"agent_end\"", StringComparison.Ordinal));
        var settledIndex = Array.FindIndex(lines, line => line.Contains("\"type\":\"agent_settled\"", StringComparison.Ordinal));
        Assert.True(agentEndIndex >= 0 && settledIndex > agentEndIndex);
        using (var end = JsonDocument.Parse(lines[agentEndIndex]))
        {
            Assert.False(end.RootElement.GetProperty("willRetry").GetBoolean());
            var messages = end.RootElement.GetProperty("messages");
            var finalMessage = messages[messages.GetArrayLength() - 1];
            Assert.Equal("assistant", finalMessage.GetProperty("role").GetString());
            Assert.Equal("aborted", finalMessage.GetProperty("stopReason").GetString());
        }
        Assert.Contains(output.Lines(), line => line.Contains("\"command\":\"abort\",\"success\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedProviderRunEmitsPiErrorMessageInAgentEndBeforeSettlement()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null, "fixture");
        var run = await ConversationRun.OpenAsync(new PiAgent(new PartialFailureClient(), new CodingTools(Path.GetTempPath()),
            retryPolicy: new ProviderRetryPolicy(maxRetries: 2)), session);
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
        channel.Writer.TryWrite("{\"id\":\"failed\",\"type\":\"prompt\",\"message\":\"hello\"}");
        await WaitForAsync(output, "agent_settled");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        var lines = output.Lines();
        var agentEndIndex = Array.FindIndex(lines, line => line.Contains("\"type\":\"agent_end\"", StringComparison.Ordinal));
        var settledIndex = Array.FindIndex(lines, line => line.Contains("\"type\":\"agent_settled\"", StringComparison.Ordinal));
        Assert.True(agentEndIndex >= 0 && settledIndex > agentEndIndex);
        using var end = JsonDocument.Parse(lines[agentEndIndex]);
        Assert.False(end.RootElement.GetProperty("willRetry").GetBoolean());
        var messages = end.RootElement.GetProperty("messages");
        var failedAssistant = messages[messages.GetArrayLength() - 1];
        Assert.Equal("assistant", failedAssistant.GetProperty("role").GetString());
        Assert.Equal("partial", failedAssistant.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("error", failedAssistant.GetProperty("stopReason").GetString());
        Assert.Equal("failed after output", failedAssistant.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task AbortRetryCancelsScheduledRpcRetryAndProjectsPiEventShapes()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var client = new RetryableRpcClient();
        Func<TimeSpan, CancellationToken, Task> retryDelay = (_, token) =>
            Task.Delay(Timeout.InfiniteTimeSpan, token);
        var retryPolicy = new AgentRunRetryPolicy(enabled: true, maxRetries: 2,
            baseDelay: TimeSpan.FromSeconds(30), maxDelay: TimeSpan.FromSeconds(30));
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()),
            retryPolicy: ProviderRetryPolicy.None), new ConversationSession(Path.GetTempPath(), "fixture", null),
            retryPolicy: retryPolicy, retryDelay: retryDelay);
        bool? persistedRetryEnabled = null;
        var service = new RpcMode(new CommandReader(channel.Reader), output, run,
            persistRetryEnabled: (enabled, token) =>
            {
                token.ThrowIfCancellationRequested();
                persistedRetryEnabled = enabled;
                run.SetAutoRetryEnabled(enabled);
                return Task.CompletedTask;
            });
        var serving = service.ServeAsync();

        channel.Writer.TryWrite("{\"id\":\"prompt\",\"type\":\"prompt\",\"message\":\"hello\"}");
        await WaitForAsync(output, "auto_retry_start");
        channel.Writer.TryWrite("{\"id\":\"abort-retry\",\"type\":\"abort_retry\"}");
        await WaitForAsync(output, "\"id\":\"abort-retry\"");
        await WaitForAsync(output, "agent_settled");
        channel.Writer.TryWrite("{\"id\":\"disable-retry\",\"type\":\"set_auto_retry\",\"enabled\":false}");
        await WaitForAsync(output, "\"id\":\"disable-retry\"");
        channel.Writer.TryWrite("{\"id\":\"invalid-retry\",\"type\":\"set_auto_retry\",\"enabled\":\"false\"}");
        await WaitForAsync(output, "\"id\":\"invalid-retry\"");
        channel.Writer.TryWrite("{\"id\":\"noop-abort\",\"type\":\"abort_retry\"}");
        await WaitForAsync(output, "\"id\":\"noop-abort\"");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        var lines = output.Lines();
        var agentStarts = lines.Where(line => line.Contains("\"type\":\"agent_start\"", StringComparison.Ordinal)).ToArray();
        var agentEnds = lines.Where(line => line.Contains("\"type\":\"agent_end\"", StringComparison.Ordinal)).ToArray();
        Assert.Single(agentStarts);
        using var failed = JsonDocument.Parse(Assert.Single(agentEnds));
        Assert.True(failed.RootElement.GetProperty("willRetry").GetBoolean());
        var startLine = Assert.Single(lines, line => line.Contains("\"type\":\"auto_retry_start\"", StringComparison.Ordinal));
        using var start = JsonDocument.Parse(startLine);
        Assert.Equal(1, start.RootElement.GetProperty("attempt").GetInt32());
        Assert.Equal(2, start.RootElement.GetProperty("maxAttempts").GetInt32());
        Assert.True(start.RootElement.GetProperty("delayMs").GetInt64() > 0);
        var endLine = Assert.Single(lines, line => line.Contains("\"type\":\"auto_retry_end\"", StringComparison.Ordinal));
        using var end = JsonDocument.Parse(endLine);
        Assert.False(end.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Retry cancelled", end.RootElement.GetProperty("finalError").GetString());
        var settledIndex = Array.FindIndex(lines, line => line.Contains("\"type\":\"agent_settled\"", StringComparison.Ordinal));
        Assert.True(settledIndex > Array.IndexOf(lines, endLine));
        Assert.Contains(lines, line => line.Contains("\"command\":\"abort_retry\",\"success\":true", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"command\":\"set_auto_retry\",\"success\":true", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"command\":\"set_auto_retry\",\"success\":false", StringComparison.Ordinal));
        Assert.False(persistedRetryEnabled);
        Assert.Equal(1, client.Requests);
    }

    [Fact]
    public async Task SuccessfulRpcRetryHasOrderedAgentAndRetryEventsForEachAttempt()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var client = new RetryOnceRpcClient();
        var retryPolicy = new AgentRunRetryPolicy(enabled: true, maxRetries: 1,
            baseDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero);
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath()),
            retryPolicy: ProviderRetryPolicy.None), new ConversationSession(Path.GetTempPath(), "fixture", null),
            retryPolicy: retryPolicy);
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();
        channel.Writer.TryWrite("{\"id\":\"prompt\",\"type\":\"prompt\",\"message\":\"hello\"}");
        await WaitForAsync(output, "agent_settled");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        var lines = output.Lines();
        var eventTypes = lines.Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var types = eventTypes.Select(document => document.RootElement.TryGetProperty("type", out var type)
                ? type.GetString() : null).ToArray();
            Assert.Equal(2, types.Count(type => type == "agent_start"));
            Assert.Equal(2, types.Count(type => type == "agent_end"));
            Assert.Equal(1, types.Count(type => type == "auto_retry_start"));
            Assert.Equal(1, types.Count(type => type == "auto_retry_end"));
            var firstEndIndex = Array.FindIndex(eventTypes, document => document.RootElement.TryGetProperty("type", out var type) &&
                type.GetString() == "agent_end");
            var startIndex = Array.FindIndex(eventTypes, document => document.RootElement.TryGetProperty("type", out var type) &&
                type.GetString() == "auto_retry_start");
            var secondStartIndex = Array.FindLastIndex(eventTypes, document => document.RootElement.TryGetProperty("type", out var type) &&
                type.GetString() == "agent_start");
            var retryEndIndex = Array.FindIndex(eventTypes, document => document.RootElement.TryGetProperty("type", out var type) &&
                type.GetString() == "auto_retry_end");
            var secondEndIndex = Array.FindLastIndex(eventTypes, document => document.RootElement.TryGetProperty("type", out var type) &&
                type.GetString() == "agent_end");
            var settledIndex = Array.FindIndex(eventTypes, document => document.RootElement.TryGetProperty("type", out var type) &&
                type.GetString() == "agent_settled");
            Assert.True(firstEndIndex < startIndex && startIndex < secondStartIndex && secondStartIndex < retryEndIndex &&
                retryEndIndex < secondEndIndex && secondEndIndex < settledIndex);
            Assert.True(eventTypes[firstEndIndex].RootElement.GetProperty("willRetry").GetBoolean());
            Assert.False(eventTypes[secondEndIndex].RootElement.GetProperty("willRetry").GetBoolean());
            Assert.Equal(2, client.Requests);
        }
        finally { foreach (var document in eventTypes) document.Dispose(); }
    }

    private static Task WaitForAsync(LockedWriter output, string fragment) => output.WaitForLineAsync(fragment);

    private sealed class CommandReader(ChannelReader<string> channel) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try { return await channel.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }
    }

    private sealed class LockedWriter : StringWriter
    {
        private readonly object _gate = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteAsync(string? value)
        {
            lock (_gate)
            {
                Write(value);
                SignalChangedUnsafe();
            }
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(string? value)
        {
            lock (_gate)
            {
                WriteLine(value);
                SignalChangedUnsafe();
            }
            return Task.CompletedTask;
        }

        private void SignalChangedUnsafe()
        {
            var changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }

        public async Task WaitForLineAsync(string fragment)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (ToString().Contains(fragment, StringComparison.Ordinal)) return;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(timeout.Token);
            }
        }

        public string[] Lines() { lock (_gate) return ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries); }
    }

    private sealed class OrderedRpcQueueClient : IChatClient
    {
        public List<string> LatestUserByRequest { get; } = [];
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LatestUserByRequest.Add(messages.Last(message => message.Role == ChatRole.User).Text!);
            if (LatestUserByRequest.Count == 1)
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "reply");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class QueuedClient : IChatClient
    {
        public int Requests { get; private set; }
        public bool SecondRequestSawFirstTurn { get; private set; }
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            if (Requests == 1)
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "first reply");
                yield break;
            }
            var snapshot = messages.ToArray();
            SecondRequestSawFirstTurn = snapshot.Any(message => message.Role == ChatRole.User && message.Text == "one") &&
                snapshot.Any(message => message.Role == ChatRole.Assistant && message.Text == "first reply") &&
                snapshot.Any(message => message.Role == ChatRole.User && message.Text == "two");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "second reply");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class StubClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "summary")]));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { yield return new ChatResponseUpdate(ChatRole.Assistant, "reply"); await Task.CompletedTask; }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SummaryFailureClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(new IOException("summary unavailable"));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (cancellationToken.IsCancellationRequested) yield break;
            throw new InvalidOperationException("A prompt was sent before preflight completed.");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class BlockingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); yield break; }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class PartialBlockingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class PartialFailureClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            await Task.Yield();
            throw new IOException("failed after output");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RetryableRpcClient : IChatClient
    {
        public int Requests { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial");
            await Task.Yield();
            throw new HttpRequestException("503 service unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RetryOnceRpcClient : IChatClient
    {
        public int Requests { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (++Requests == 1)
            {
                await Task.Yield();
                throw new HttpRequestException("503 service unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable);
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "recovered");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
