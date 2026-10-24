using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class ReadImageModelContextTests
{
    private const string NonVisionNote = "[Current model does not support images. The image will be omitted from this request.]";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentModelNoteIsCanonicalAndUpdatesWhenTheModelChanges(bool corrupt)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "tools", "parity", "fixtures",
            "builtin-coding-tool-read-images.json")));
        var image = Convert.FromBase64String(assets.RootElement.GetProperty("assets")
            .GetProperty(corrupt ? "png-rgb-truncated" : "png-rgb").GetProperty("base64").GetString()!);
        var root = Path.Combine(Path.GetTempPath(), "pisharp-read-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "image.dat"), image);
            var firstClient = new ReadClient("text-model-read");
            var agent = new PiAgent(firstClient, new CodingTools(root), supportsImages: false);
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(agent, conversation);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("inspect the image")) events.Add(item);

            var finished = Assert.Single(events, item => item.Type == "tool_execution_finished");
            var visionText = "Read image file [image/png]" + (corrupt ? "\n[Image omitted: could not be resized below the inline image size limit.]" : "");
            Assert.Equal(visionText + "\n" + NonVisionNote, finished.Text);
            Assert.Equal(finished.Text, firstClient.ToolText);
            Assert.False(firstClient.SawImages);
            Assert.Equal(finished.Text, corrupt ? finished.StructuredContent!.Value.GetString() :
                finished.StructuredContent!.Value.GetProperty("note").GetString());
            var restored = ConversationSession.Parse(conversation.ToJson());
            var stored = Assert.Single(restored.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>());
            Assert.True(ToolResultOutput.TryReadContract(stored, out var storedContract));
            Assert.Equal(finished.Text, storedContract.Text);

            var nextClient = new ReadClient("vision-model-read");
            agent.SetModelRuntime(nextClient, supportsImages: true, imageResizeOptions: null);
            events.Clear();
            await foreach (var item in run.RunEventsAsync("inspect with the vision model")) events.Add(item);
            var next = Assert.Single(events, item => item.Type == "tool_execution_finished");
            Assert.Equal(visionText, next.Text);
            Assert.Equal(visionText, nextClient.ToolText);
            Assert.Equal(!corrupt, nextClient.SawImages);
            Assert.Equal(2, conversation.ActiveMessages().SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ReadClient(string callId) : IChatClient
    {
        private int _requests;
        public string? ToolText { get; private set; }
        public bool SawImages { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(callId, "read",
                    new Dictionary<string, object?> { ["path"] = "image.dat" })]);
            else
            {
                var snapshot = messages.ToArray();
                ToolText = Assert.IsType<string>(snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                    .Single(result => result.CallId == callId).Result);
                SawImages = snapshot.SelectMany(message => message.Contents).OfType<DataContent>().Any();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
