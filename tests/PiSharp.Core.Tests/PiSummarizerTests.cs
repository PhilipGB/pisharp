using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class PiSummarizerTests
{
    [Fact]
    public async Task CompactionUsesCreatePromptWithoutPreviousSummary()
    {
        var client = new CapturingChatClient();
        var summarizer = new PiSummarizer(client, RetryPolicyOptions.Disabled, () => 2048);

        await summarizer.GenerateCompactionAsync(Plan(previousSummary: null), null, CancellationToken.None);

        var prompt = client.LastUserPrompt;
        Assert.Contains("Create a structured context checkpoint summary", prompt);
        Assert.DoesNotContain("<previous-summary>", prompt);
    }

    [Fact]
    public async Task CompactionUsesUpdatePromptWithPreviousSummary()
    {
        var client = new CapturingChatClient();
        var summarizer = new PiSummarizer(client, RetryPolicyOptions.Disabled, () => 2048);

        await summarizer.GenerateCompactionAsync(Plan(previousSummary: "## Goal\nprevious work"), null, CancellationToken.None);

        var prompt = client.LastUserPrompt;
        Assert.Contains("Update the existing structured summary", prompt);
        Assert.Contains("<previous-summary>\n## Goal\nprevious work\n</previous-summary>", prompt);
    }

    [Fact]
    public async Task CompactionAppendsFileOperationSectionsAndDetails()
    {
        var client = new CapturingChatClient();
        var summarizer = new PiSummarizer(client, RetryPolicyOptions.Disabled, () => 2048);
        var plan = new CompactionPlan(
            "keep-1",
            [Message("a", "one")],
            [],
            false,
            100,
            null,
            new CompactionFileOperations(["README.md"], ["src/Program.cs"]),
            new CompactionSettings());

        var result = await summarizer.GenerateCompactionAsync(plan, null, CancellationToken.None);

        Assert.Contains("<read-files>\nREADME.md\n</read-files>", result.Summary);
        Assert.Contains("<modified-files>\nsrc/Program.cs\n</modified-files>", result.Summary);
        Assert.Equal("keep-1", result.FirstKeptEntryId);
        var details = result.Details ?? throw new InvalidOperationException("compaction details are missing.");
        var readFiles = details.GetProperty("readFiles").EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();
        Assert.Contains("README.md", readFiles);
    }

    private static CompactionPlan Plan(string? previousSummary) => new(
        "keep-1",
        [Message("a", "one"), Message("b", "two")],
        [],
        false,
        500,
        previousSummary,
        new CompactionFileOperations([], []),
        new CompactionSettings());

    private static MessageEntry Message(string id, string text) => new(
        id,
        null,
        DateTimeOffset.UtcNow,
        JsonSerializer.SerializeToElement(new { role = "user", content = text }));

    private sealed class CapturingChatClient : IChatClient
    {
        public string? LastUserPrompt { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var user = messages.LastOrDefault(message => message.Role == ChatRole.User);
            LastUserPrompt = user?.Text;
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "## Goal\nsummarized"));
            response.Usage = new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 5,
                TotalTokenCount = 15,
            };
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            StreamAsync(cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public IChatClient WithInstructions(string? instructions) => this;

        public IChatClient WithTools(IEnumerable<AITool> tools) => this;

        public IChatClient WithTools(params AIFunction[] functions) => this;

        public IChatClient WithFunctions(params AIFunction[] functions) => this;

        public IChatClient WithFunctions(IEnumerable<AIFunction> functions) => this;

        public object? GetService(Type serviceType, object? serviceKey) => null;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
