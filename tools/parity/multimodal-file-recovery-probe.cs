using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

var root = args[0];
var broken = Convert.FromBase64String(args[1]);
var valid = Convert.FromBase64String(args[2]);
await File.WriteAllBytesAsync(Path.Combine(root, "broken.png"), broken);
await File.WriteAllBytesAsync(Path.Combine(root, "valid.png"), valid);
var processed = await CliFileArguments.ProcessFilesAsync("inspect", ["broken.png", "valid.png"], root);
var client = new CaptureClient();
var conversation = new ConversationSession(root, "fixture", null);
var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noTools: true), conversation);
var prompt = new ChatMessage(ChatRole.User,
    processed.Images.Cast<AIContent>().Prepend(new TextContent(processed.Text)).ToArray());
await foreach (var _ in run.RunStreamingAsync(prompt, processed.Text)) { }
var user = client.Requests.Single().First(message => message.Role == ChatRole.User);
var parts = user.Contents.Select(content => content switch
{
    TextContent text => (object)new { type = "text", text = text.Text },
    DataContent image when image.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true =>
        new { type = "image", mimeType = image.MediaType, data = Convert.ToBase64String(image.Data.ToArray()) },
    _ => new { type = "other", text = content.GetType().Name }
}).ToArray();
Console.WriteLine(JsonSerializer.Serialize(parts));

sealed class CaptureClient : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.Select(message => new ChatMessage(message.Role, message.Contents.ToArray())).ToArray());
        yield return new ChatResponseUpdate(ChatRole.Assistant, "captured");
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
