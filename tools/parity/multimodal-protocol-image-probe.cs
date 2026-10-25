using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

var directory = args[0];
var imagePath = Path.Combine(directory, "pixel.png");
var imageBytes = await File.ReadAllBytesAsync(imagePath);
var json = await CaptureJsonAsync(directory, imagePath);
var rpc = await CaptureRpcAsync(directory, imageBytes);
await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { json, rpc }));

static async Task<IReadOnlyList<object>> CaptureJsonAsync(string directory, string imagePath)
{
    var client = new CapturingClient();
    var conversation = new ConversationSession(directory, "json-fixture", null);
    var run = await ConversationRun.OpenAsync(new PiAgent(client,
        new CodingTools(directory), noTools: true), conversation);
    var resources = await ResourceCatalog.LoadAsync(directory, directory, trusted: false,
        discoverSkills: false, discoverPrompts: false);
    var cli = CliArguments.Parse(["--mode", "json", "--", "@broken.png", "@pixel.png", "Describe the fixture"]);
    using var output = new StringWriter();
    using var error = new StringWriter();
    await JsonPromptMode.RunAsync(conversation, run,
        new ConversationStore(directory, Path.Combine(directory, "sessions")), null, resources,
        cli.Prompt, "", cli, directory, output, error);
    return client.Parts;
}

static async Task<IReadOnlyList<object>> CaptureRpcAsync(string directory, byte[] imageBytes)
{
    var client = new CapturingClient();
    var conversation = new ConversationSession(directory, "rpc-fixture", null);
    var run = await ConversationRun.OpenAsync(new PiAgent(client,
        new CodingTools(directory), noTools: true), conversation);
    var command = JsonSerializer.Serialize(new
    {
        id = "image",
        type = "prompt",
        message = "Describe the fixture",
        images = new[] { new { type = "image", data = Convert.ToBase64String(imageBytes), mimeType = "image/png" } }
    });
    var channel = Channel.CreateUnbounded<string>();
    var output = new SettledWriter();
    var serving = new RpcMode(new ChannelCommandReader(channel.Reader), output, run).ServeAsync();
    channel.Writer.TryWrite(command);
    await output.Settled.Task.WaitAsync(TimeSpan.FromSeconds(15));
    channel.Writer.Complete();
    await serving.WaitAsync(TimeSpan.FromSeconds(15));
    return client.Parts;
}

sealed class ChannelCommandReader(ChannelReader<string> reader) : TextReader
{
    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try { return await reader.ReadAsync(cancellationToken); }
        catch (ChannelClosedException) { return null; }
    }
}

sealed class SettledWriter : StringWriter
{
    public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task WriteAsync(string? value)
    {
        if (value?.Contains("agent_settled", StringComparison.Ordinal) == true) Settled.TrySetResult();
        return base.WriteAsync(value);
    }
}

sealed class CapturingClient : IChatClient
{
    public List<object> Parts { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var user = messages.Last(message => message.Role == ChatRole.User);
        Parts.AddRange(user.Contents.Select(content => content switch
        {
            TextContent text => (object)new { type = "text", text = text.Text },
            DataContent image => new { type = "image", mimeType = image.MediaType, data = Convert.ToBase64String(image.Data.ToArray()) },
            _ => new { type = "other", mimeType = (string?)null, data = (string?)null }
        }));
        yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
