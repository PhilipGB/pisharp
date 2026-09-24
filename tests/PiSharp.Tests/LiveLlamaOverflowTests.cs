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
        var model = new ModelDescriptor(modelId, modelId, null, "fixture", Provider: "fixture", Api: "openai-completions");
        var profile = new ProviderProfile("fixture", "fixture", endpoint, true, false, null, null, [model]);
        var selection = new ModelSelection(profile, model, "not-needed", true, "fixture");
        var conversation = new ConversationSession(Path.GetTempPath(), modelId, null);
        conversation.Append(new ChatMessage(ChatRole.User, raw));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "Previous turn finished."));
        var run = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(selection),
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
}
