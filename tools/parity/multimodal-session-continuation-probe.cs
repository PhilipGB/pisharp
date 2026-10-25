using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

const string tinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";
var root = args[0];
var outputPath = args[1];
Directory.CreateDirectory(root);
var store = new ConversationStore(root, Path.Combine(root, "sessions"));
var conversation = new ConversationSession(root, "fixture", null);
var sessionPath = store.NewPath(conversation);
var imageBytes = Convert.FromBase64String(tinyPng);
var firstClient = new ImageCaptureClient();
var firstRun = await ConversationRun.OpenAsync(
    new PiAgent(firstClient, new CodingTools(root), noTools: true), conversation);
await foreach (var _ in firstRun.RunStreamingAsync(new ChatMessage(ChatRole.User,
    [new TextContent("look"), new DataContent(imageBytes, "image/png")]), "look")) { }
await store.SaveAsync(conversation, sessionPath);

var reopened = await store.LoadAsync(sessionPath);
var restoredUser = reopened.ActiveMessages().First(message => message.Role == ChatRole.User);
var restoredUserParts = Summarize(restoredUser.Contents);
var continuationClient = new ImageCaptureClient();
var resumedRun = await ConversationRun.OpenAsync(
    new PiAgent(continuationClient, new CodingTools(root), noTools: true), reopened);
await foreach (var _ in resumedRun.RunStreamingAsync("continue")) { }

var continuationUser = continuationClient.Requests.Single()
    .First(message => message.Role == ChatRole.User);
var report = new
{
    initialProviderParts = firstClient.Requests.Single()
        .First(message => message.Role == ChatRole.User) is { } initialUser ? Summarize(initialUser.Contents) : [],
    continuationProviderParts = Summarize(continuationUser.Contents),
    restoredUser = restoredUserParts
};
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report));

static object[] Summarize(IList<AIContent> contents) => contents.Select(content => content switch
{
    TextContent text => (object)new { type = "text", text = text.Text },
    DataContent image when image.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true =>
        new { type = "image", mimeType = image.MediaType, data = Convert.ToBase64String(image.Data.ToArray()) },
    _ => new { type = "other", text = content.GetType().Name }
}).ToArray();

sealed class ImageCaptureClient : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.Select(message => new ChatMessage(message.Role, message.Contents.ToArray())).ToArray());
        yield return new ChatResponseUpdate(ChatRole.Assistant, "received");
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
