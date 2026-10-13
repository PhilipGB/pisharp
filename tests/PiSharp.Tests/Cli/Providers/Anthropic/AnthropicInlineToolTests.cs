using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Tests;

public sealed class AnthropicInlineToolTests
{
    [Fact]
    public async Task OAuthUsesClaudeCodeNamesInInitialDefinitionsAndInlineRemovals()
    {
        var session = Import("""
            {"role":"system","content":"base","toolsAdded":[{"name":"bash","description":"shell","parameters":{"type":"object","properties":{}}}]}
            {"role":"user","content":"before"}
            {"role":"system","content":"","toolsRemoved":[{"name":"bash"}],"toolsAdded":[{"name":"read","description":"reader","parameters":{"type":"object","properties":{}}}]}
            """);
        var (payload, beta) = await Capture(session.ContextMessages(), true, true, oauth: true);
        Assert.Equal("Bash", payload.GetProperty("tools")[0].GetProperty("name").GetString());
        var update = payload.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("Bash", update[0].GetProperty("tool").GetProperty("name").GetString());
        Assert.Equal("Read", update[1].GetProperty("tool").GetProperty("definition").GetProperty("name").GetString());
        Assert.Equal("You are Claude Code, Anthropic's official CLI for Claude.", payload.GetProperty("system")[0].GetProperty("text").GetString());
        Assert.Contains("oauth-2025-04-20", beta);
    }

    [Fact]
    public async Task LiveAgentToolChangesAndResumeUseTheFrozenPrefixOnTheActualWire()
    {
        var (payload, beta) = await Capture([], true, true, streaming: true, requestCount: 3,
            invoke: async (client, token) =>
            {
                var registration = new ExtensionRegistration();
                registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(() => "base", name: "base_tool", description: "original")));
                PiAgent Agent() => new(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
                    systemPrompt: "base prompt", extensionToolRegistrations: registration.ToolDefinitions,
                    liveExtensionRegistration: registration);
                var conversation = new ConversationSession(Path.GetTempPath(), "claude-opus-5", null);
                var run = await ConversationRun.OpenAsync(Agent(), conversation);
                await foreach (var _ in run.RunEventsAsync("before", token)) { }
                registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(() => "late", name: "late_tool", description: "later")));
                await foreach (var _ in run.RunEventsAsync("after", token)) { }
                var resumed = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(conversation));
                var restored = await ConversationRun.OpenAsync(Agent(), resumed);
                await foreach (var _ in restored.RunEventsAsync("resume", token)) { }
            });
        Assert.Contains("inline-tools-2026-09-15", beta);
        Assert.Equal(["base_tool", "__pi_deferred_placeholder__"], payload.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
        var system = Assert.Single(payload.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "system");
        var definition = Assert.Single(system.GetProperty("content").EnumerateArray()).GetProperty("tool").GetProperty("definition");
        Assert.Equal("late_tool", definition.GetProperty("name").GetString());
        Assert.Equal("later", definition.GetProperty("description").GetString());
    }

    [Fact]
    public async Task NativeToolUpdatesKeepInitialDefinitionsAndDefineLaterToolsByValue()
    {
        var session = Import("""
            {"role":"system","content":"base prompt","sections":{"rules":"old rules","docs":"read docs"},"toolsAdded":[{"name":"base_tool","description":"base tool","parameters":{"type":"object","properties":{},"required":[]}}]}
            {"role":"user","content":"before"}
            {"role":"system","content":"updated guidance","sections":{"rules":"new rules","docs":null},"toolsRemoved":[{"name":"base_tool"}],"toolsAdded":[{"name":"late_tool","description":"late tool","parameters":{"type":"object","properties":{},"required":[]}}]}
            """);
        var (payload, beta) = await Capture(session.ContextMessages(), true, true);
        Assert.Contains("inline-tools-2026-09-15", beta);
        Assert.DoesNotContain("mid-conversation-tool-changes-2026-07-01", beta);
        var tools = payload.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(["base_tool", "__pi_deferred_placeholder__"], tools.Select(tool => tool.GetProperty("name").GetString()));
        Assert.Equal("ephemeral", tools[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.True(tools[1].GetProperty("defer_loading").GetBoolean());
        Assert.False(tools[1].TryGetProperty("cache_control", out _));
        Assert.Equal("base prompt\n\nold rules\n\nread docs", payload.GetProperty("system")[0].GetProperty("text").GetString());
        var update = payload.GetProperty("messages")[1];
        Assert.Equal("system", update.GetProperty("role").GetString());
        var blocks = update.GetProperty("content");
        Assert.Equal(["text", "tool_removal", "tool_addition"], blocks.EnumerateArray().Select(block => block.GetProperty("type").GetString()));
        Assert.Equal("updated guidance\n\nUpdated system prompt section \"rules\":\n\nnew rules\n\nRemoved system prompt section \"docs\".", blocks[0].GetProperty("text").GetString());
        Assert.Equal("base_tool", blocks[1].GetProperty("tool").GetProperty("name").GetString());
        var definition = blocks[2].GetProperty("tool").GetProperty("definition");
        Assert.Equal("late_tool", definition.GetProperty("name").GetString());
        Assert.False(definition.TryGetProperty("cache_control", out _));
        Assert.False(definition.TryGetProperty("defer_loading", out _));
        Assert.Equal("ephemeral", blocks[2].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task SameNameRedefinitionDoesNotEmitRemovalAndRetainsOriginalDeclaration()
    {
        var session = Import("""
            {"role":"system","content":"base","toolsAdded":[{"name":"base_tool","description":"original","parameters":{"type":"object","properties":{}}}]}
            {"role":"user","content":"before"}
            {"role":"system","content":"","toolsRemoved":[{"name":"base_tool"}],"toolsAdded":[{"name":"base_tool","description":"changed","parameters":{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}}]}
            """);
        var (payload, _) = await Capture(session.ContextMessages(), true, true);
        Assert.Equal("original", payload.GetProperty("tools")[0].GetProperty("description").GetString());
        var block = Assert.Single(payload.GetProperty("messages")[1].GetProperty("content").EnumerateArray());
        Assert.Equal("tool_addition", block.GetProperty("type").GetString());
        var definition = block.GetProperty("tool").GetProperty("definition");
        Assert.Equal("changed", definition.GetProperty("description").GetString());
        Assert.Equal("value", Assert.Single(definition.GetProperty("input_schema").GetProperty("required").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task NativeToolChangesRequireBothCapabilities(bool systemSupport, bool toolSupport)
    {
        var session = Import("""
            {"role":"system","content":"base","toolsAdded":[{"name":"base_tool","description":"original","parameters":{"type":"object","properties":{}}}]}
            {"role":"user","content":"before"}
            {"role":"system","content":"updated","toolsRemoved":[{"name":"base_tool"}],"toolsAdded":[{"name":"late_tool","description":"late","parameters":{"type":"object","properties":{}}}]}
            """);
        var (payload, beta) = await Capture(session.ContextMessages(), systemSupport, toolSupport);
        Assert.DoesNotContain("inline-tools-2026-09-15", beta);
        Assert.Equal("late_tool", Assert.Single(payload.GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
        if (systemSupport)
            Assert.Equal("text", Assert.Single(payload.GetProperty("messages")[1].GetProperty("content").EnumerateArray()).GetProperty("type").GetString());
        else
        {
            Assert.Single(payload.GetProperty("messages").EnumerateArray());
            Assert.Equal("base\n\nupdated", payload.GetProperty("system")[0].GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task NoInitialToolUsesCurrentToolsWithoutDeferredPlaceholder()
    {
        var session = Import("""
            {"role":"system","content":"base"}
            {"role":"user","content":"before"}
            {"role":"system","content":"updated","toolsAdded":[{"name":"late_tool","description":"late","parameters":{"type":"object","properties":{}}}]}
            """);
        var (payload, beta) = await Capture(session.ContextMessages(), true, true);
        Assert.DoesNotContain("inline-tools-2026-09-15", beta);
        Assert.Equal("late_tool", Assert.Single(payload.GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task PendingSystemUpdatesFollowToolResultsAndPrecedeTheNextAssistant()
    {
        var session = Import("""
            {"role":"system","content":"base","toolsAdded":[{"name":"base_tool","description":"original","parameters":{"type":"object","properties":{}}}]}
            {"role":"user","content":"before"}
            {"role":"assistant","api":"anthropic-messages","provider":"anthropic","model":"claude-opus-5","stopReason":"toolUse","content":[{"type":"toolCall","name":"base_tool","id":"call_1","arguments":{}}]}
            {"role":"system","content":"updated","toolsRemoved":[{"name":"base_tool"}]}
            {"role":"toolResult","toolCallId":"call_1","toolName":"base_tool","content":[{"type":"text","text":"done"}],"isError":false}
            {"role":"user","content":"continue"}
            {"role":"assistant","api":"anthropic-messages","provider":"anthropic","model":"claude-opus-5","stopReason":"stop","content":[{"type":"text","text":"finished"}]}
            """);
        var (payload, _) = await Capture(session.ContextMessages(), true, true);
        var messages = payload.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(["user", "assistant", "user", "user", "system", "assistant"], messages.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("tool_result", messages[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("call_1", messages[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("done", messages[2].GetProperty("content")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task SharedRuntimeDoesNotSynthesizeAResultBeforeAnInterveningSystemUpdate()
    {
        var session = Import("""
            {"role":"system","content":"base","toolsAdded":[{"name":"base_tool","description":"original","parameters":{"type":"object","properties":{}}}]}
            {"role":"user","content":"before"}
            {"role":"assistant","api":"anthropic-messages","provider":"anthropic","model":"claude-opus-5","stopReason":"toolUse","content":[{"type":"toolCall","name":"base_tool","id":"call_1","arguments":{}}]}
            {"role":"system","content":"updated","toolsRemoved":[{"name":"base_tool"}]}
            {"role":"toolResult","toolCallId":"call_1","toolName":"base_tool","content":[{"type":"text","text":"done"}],"isError":false}
            """);
        var (payload, _) = await Capture([], true, true, streaming: true, invoke: async (client, token) =>
        {
            var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noTools: true);
            var run = await ConversationRun.OpenAsync(agent, session);
            await foreach (var _ in run.RunEventsAsync("continue", token)) { }
        });
        var results = payload.GetProperty("messages").EnumerateArray().SelectMany(message => message.GetProperty("content").EnumerateArray())
            .Where(block => block.GetProperty("type").GetString() == "tool_result").ToArray();
        Assert.Equal("done", Assert.Single(results).GetProperty("content").GetString());
    }

    [Fact]
    public void SystemMetadataSurvivesNativePersistenceBranchSelectionAndPiExport()
    {
        var session = Import("""
            {"role":"system","content":"base","sections":{"rules":"old"},"toolsAdded":[{"name":"base_tool","description":"original","parameters":{"type":"object","properties":{}}}]}
            {"role":"user","content":"before"}
            """);
        var message = session.ContextMessages()[0];
        Assert.NotNull(message.AdditionalProperties);
        var native = new ConversationSession(Path.GetTempPath(), "fixture", null);
        native.Append(message);
        var exported = PiJsonlSessionInterchange.Export(native);
        using var record = JsonDocument.Parse(exported.Split('\n')[1]);
        Assert.Equal("base_tool", record.RootElement.GetProperty("message").GetProperty("toolsAdded")[0].GetProperty("name").GetString());
        Assert.Equal("old", record.RootElement.GetProperty("message").GetProperty("sections").GetProperty("rules").GetString());
        var restored = ConversationSession.Parse(native.ToJson());
        Assert.Equal(message.Text, restored.ContextMessages()[0].Text);
        Assert.NotNull(restored.ContextMessages()[0].AdditionalProperties);
    }

    internal static ConversationSession Import(string messages)
    {
        var header = JsonSerializer.Serialize(new { type = "session", version = 3, id = "fixture", cwd = Path.GetTempPath() });
        string? parentId = null;
        var entries = new List<string> { header };
        foreach (var line in messages.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var message = JsonDocument.Parse(line);
            var id = $"entry{entries.Count}";
            entries.Add(JsonSerializer.Serialize(new { type = "message", id, parentId, timestamp = "2026-10-02T00:00:00.000Z", message = message.RootElement }));
            parentId = id;
        }
        return PiJsonlSessionInterchange.Import(string.Join('\n', entries));
    }

    internal static async Task<(JsonElement Payload, string Beta)> Capture(IReadOnlyList<ChatMessage> messages,
        bool systemSupport, bool toolSupport, bool streaming = false, int requestCount = 1,
        Func<IChatClient, CancellationToken, Task>? invoke = null, bool oauth = false)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        JsonElement payload = default;
        var beta = "";
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < requestCount; index++)
            {
                var request = await listener.GetContextAsync();
                using var body = await JsonDocument.ParseAsync(request.Request.InputStream);
                payload = body.RootElement.Clone();
                beta = request.Request.Headers["anthropic-beta"] ?? "";
                request.Response.ContentType = streaming ? "text/event-stream" : "application/json";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                var response = """
                {"id":"msg_fixture","type":"message","role":"assistant","model":"claude-opus-5","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":4,"output_tokens":2}}
                """;
                await writer.WriteAsync(streaming ? """
                event: message_start
                data: {"type":"message_start","message":{"id":"msg_fixture","type":"message","role":"assistant","model":"claude-opus-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":4,"output_tokens":0}}}

                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":0}

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":2}}

                event: message_stop
                data: {"type":"message_stop"}


                """ : response);
                await writer.FlushAsync();
                request.Response.Close();
            }
        });
        var compatibility = JsonSerializer.SerializeToElement(new { supportsMidConvoSystemMessages = systemSupport, supportsMidConvoToolChanges = toolSupport });
        var model = new ModelDescriptor("claude-opus-5", "anthropic", 100000, "fixture", Provider: "anthropic", Api: "anthropic-messages", Compatibility: compatibility);
        var profile = new ProviderProfile("anthropic", "Anthropic", new Uri($"http://127.0.0.1:{port}"), true, false, null, null, [model], Api: "anthropic-messages");
        using var client = ProviderChatClientFactory.Create(new ModelSelection(profile, model, "fixture-key", true, "fixture")
        { AnthropicIsOAuthToken = oauth, AnthropicAuthToken = oauth ? "sk-ant-oat-fixture" : null });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (invoke is not null) await invoke(client, deadline.Token);
        else if (streaming)
            await foreach (var _ in client.GetStreamingResponseAsync(messages, cancellationToken: deadline.Token)) { }
        else
            Assert.Equal("ok", (await client.GetResponseAsync(messages, cancellationToken: deadline.Token)).Text);
        await server.WaitAsync(deadline.Token);
        return (payload, beta);
    }
}
