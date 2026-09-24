using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

/// <summary>Explicitly opt-in: sends a large request to a real llama.cpp server. Never runs in CI by default.</summary>
public sealed class LiveLlamaOverflowTests
{
    [Fact]
    public async Task ActualLlamaServerReadsFileAndContinuesAfterToolResult()
    {
        var endpointText = Environment.GetEnvironmentVariable("PISHARP_TEST_LLAMA_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpointText)) return; // Explicit live opt-in only.
        var endpoint = new Uri(endpointText, UriKind.Absolute);
        var modelId = Environment.GetEnvironmentVariable("PISHARP_TEST_LLAMA_MODEL") ?? ConnectionSettings.LocalModel;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-live-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cwd, "live-fixture.txt"), "PISHARP_LIVE_TOOL_SENTINEL");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var conversation = new ConversationSession(cwd, modelId, null);
            var run = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(Select(endpoint, modelId)),
                new CodingTools(cwd), selectedTools: ["read"]), conversation);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync(
                "Call read once with path live-fixture.txt. After the result, reply briefly. Do not call other tools.", deadline.Token)) events.Add(item);
            Assert.Contains(events, item => item.Type == "turn_completed");
            Assert.DoesNotContain(events, item => item.Type == "turn_failed");
            Assert.Contains(conversation.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionCallContent>(),
                call => call.Name == "read");
            Assert.Contains(conversation.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
                result => result.Result?.ToString()?.Contains("PISHARP_LIVE_TOOL_SENTINEL", StringComparison.Ordinal) == true);
            Assert.True(events.Count(item => item.Type == "model_request_started") >= 2);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ActualLlamaServerRecoversOverflowAfterToolWithoutRepeatingSideEffect()
    {
        var endpointText = Environment.GetEnvironmentVariable("PISHARP_TEST_LLAMA_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpointText)) return;
        var endpoint = new Uri(endpointText, UriKind.Absolute);
        var modelId = Environment.GetEnvironmentVariable("PISHARP_TEST_LLAMA_MODEL") ?? ConnectionSettings.LocalModel;
        var fixture = new LargeToolFixture();
        var tool = AIFunctionFactory.Create(fixture.Emit, name: "emit_large_fixture");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var conversation = new ConversationSession(Path.GetTempPath(), modelId, null);
        var run = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(Select(endpoint, modelId)),
            new CodingTools(Path.GetTempPath()), selectedTools: ["emit_large_fixture"], noTools: true,
            extensionTools: [tool]), conversation, autoCompaction: new AutoCompactionPolicy(1_200_000, 1000));
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync(
            "Call emit_large_fixture exactly once. After receiving its output, reply briefly with the sentinel. Do not call it again.", deadline.Token))
            events.Add(item);

        Assert.Equal(1, fixture.Calls);
        Assert.Contains(events, item => item.Type == "model_request_failed" &&
            item.Error?.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Single(events, item => item.Type == "model_context_overflow_recovery");
        Assert.Single(events, item => item.Type == "model_request_failed");
        Assert.Equal(3, events.Count(item => item.Type == "model_request_started"));
        Assert.Contains(events, item => item.Type == "turn_completed");
        Assert.Contains(conversation.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>(),
            result => result.Result?.ToString()?.Contains("PISHARP_LIVE_LARGE_TOOL_SENTINEL", StringComparison.Ordinal) == true);
        Assert.Contains(conversation.Tree.ActivePath(), node => node.Type == "context_projection");
    }

    private sealed class LargeToolFixture
    {
        public int Calls { get; private set; }

        [System.ComponentModel.Description("Emit a large deterministic diagnostic fixture. Call once only.")]
        public string Emit()
        {
            Calls++;
            return "PISHARP_LIVE_LARGE_TOOL_SENTINEL " + string.Concat(Enumerable.Repeat("ping ", 190_000));
        }
    }

    [Fact]
    public async Task ActualLlamaServerContextOverflowRetriesWithSummaryAndPreservesRawHistory()
    {
        var endpointText = Environment.GetEnvironmentVariable("PISHARP_TEST_LLAMA_ENDPOINT");
        // xUnit's installed VSTest adapter does not support dynamic skips. An unset opt-in
        // performs no live assertions; only the explicitly configured run is evidence.
        if (string.IsNullOrWhiteSpace(endpointText)) return;
        var endpoint = new Uri(endpointText, UriKind.Absolute);
        var modelId = Environment.GetEnvironmentVariable("PISHARP_TEST_LLAMA_MODEL") ?? ConnectionSettings.LocalModel;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // With the configured Qwen3.8-27B-GGUF server at 179200 tokens, this fixture
        // was measured at 190052 prompt tokens (HTTP 400 exceed_context_size_error).
        var raw = string.Concat(Enumerable.Repeat("ping ", 190_000));
        var conversation = new ConversationSession(Path.GetTempPath(), modelId, null);
        conversation.Append(new ChatMessage(ChatRole.User, raw));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "Previous turn finished."));
        var run = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(Select(endpoint, modelId)),
            new CodingTools(Path.GetTempPath()), noTools: true), conversation,
            autoCompaction: new AutoCompactionPolicy(1_200_000, 1000));
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("Reply briefly with PISHARP_OVERFLOW_RECOVERED.", deadline.Token)) events.Add(item);

        Assert.Contains(events, item => item.Type == "model_request_failed" &&
            item.Error?.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Single(events, item => item.Type == "model_context_overflow_recovery");
        Assert.Single(events, item => item.Type == "model_request_failed");
        Assert.Equal(2, events.Count(item => item.Type == "model_request_started"));
        Assert.Contains(events, item => item.Type == "turn_completed");
        Assert.Equal(raw, conversation.ActiveMessages()[0].Text);
        Assert.DoesNotContain(conversation.Tree.ActivePath(), node => node.Type == "compaction");
        Assert.Contains(conversation.Tree.ActivePath(), node => node.Type == "context_projection");
    }

    private static ModelSelection Select(Uri endpoint, string modelId)
    {
        var model = new ModelDescriptor(modelId, modelId, null, "fixture", Provider: "fixture", Api: "openai-completions");
        var profile = new ProviderProfile("fixture", "fixture", endpoint, true, false, null, null, [model]);
        return new ModelSelection(profile, model, "not-needed", true, "fixture");
    }
}
