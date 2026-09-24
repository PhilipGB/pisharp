using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class MultimodalRuntimeTests
{
    [Fact]
    public async Task ConversationRunAcceptsAndPersistsImageContentInUserMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-multimodal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var imageBytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
            var client = new ImageAwareClient();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noTools: true), conversation);
            var message = new ChatMessage(ChatRole.User,
            [
                new TextContent("What is in this picture?"),
                new DataContent(imageBytes, "image/png")
            ]);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync(message, "What is in this picture?")) events.Add(item);

            Assert.True(client.SawImage);
            var history = conversation.ActiveMessages();
            Assert.Equal("image received", history.Last(message => message.Role == ChatRole.Assistant).Text);
            var restoredImage = Assert.IsType<DataContent>(history.Single(message => message.Role == ChatRole.User).Contents[1]);
            Assert.Equal("image/png", restoredImage.MediaType);
            Assert.Equal(imageBytes, restoredImage.Data.ToArray());
            Assert.Contains(events, item => item.Type == "agent_run_completed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BlockImagesFiltersEveryProviderRequestWithoutErasingSessionHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-block-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = new DataContent(new byte[] { 0x89, 0x50, 0x4e, 0x47 }, "image/png");
            var client = new ImageAwareClient();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noTools: true,
                blockImages: true), conversation);
            var message = new ChatMessage(ChatRole.User,
                [new TextContent("look"), image, image, new TextContent("again"), image]);
            await foreach (var _ in run.RunEventsAsync(message, "look")) { }
            await foreach (var _ in run.RunEventsAsync("another turn")) { }
            Assert.False(client.SawImage);
            Assert.Equal(2, client.RequestContents.Count);
            foreach (var request in client.RequestContents)
            {
                Assert.Empty(request.OfType<DataContent>());
                Assert.Equal(2, request.OfType<TextContent>().Count(content => content.Text == "Image reading is disabled."));
            }
            Assert.Equal(3, conversation.ActiveMessages().Where(item => item.Role == ChatRole.User)
                .SelectMany(item => item.Contents).OfType<DataContent>().Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BlockingImagesOnResumeFiltersHistoricalImagesWithoutChangingStoredTurns()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resumed-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var conversation = new ConversationSession(root, "fixture", null);
            var firstClient = new ImageAwareClient();
            var first = await ConversationRun.OpenAsync(new PiAgent(firstClient, new CodingTools(root), noTools: true), conversation);
            await foreach (var _ in first.RunEventsAsync(new ChatMessage(ChatRole.User,
                [new TextContent("look"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]), "look")) { }
            Assert.True(firstClient.SawImage);
            var secondClient = new ImageAwareClient();
            var resumed = await ConversationRun.OpenAsync(new PiAgent(secondClient, new CodingTools(root), noTools: true,
                blockImages: true), conversation);
            await foreach (var _ in resumed.RunEventsAsync("continue")) { }
            Assert.False(secondClient.SawImage);
            Assert.Contains(secondClient.RequestContents.Single().OfType<TextContent>(), item => item.Text == "Image reading is disabled.");
            Assert.Single(conversation.ActiveMessages().SelectMany(item => item.Contents).OfType<DataContent>());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ImageAwareClient : IChatClient
    {
        public bool SawImage { get; private set; }
        public List<IReadOnlyList<AIContent>> RequestContents { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestContents.Add(messages.SelectMany(message => message.Contents).ToArray());
            SawImage = messages.SelectMany(message => message.Contents).OfType<DataContent>()
                .Any(content => content.MediaType == "image/png");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "image received");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
