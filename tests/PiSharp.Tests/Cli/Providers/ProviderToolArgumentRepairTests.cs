using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class ProviderToolArgumentRepairTests
{
    [Theory]
    [InlineData("openai-completions", false)]
    [InlineData("openai-completions", true)]
    [InlineData("openai-responses", false)]
    [InlineData("openai-responses", true)]
    [InlineData("anthropic-messages", true)]
    public async Task CompletedMalformedArgumentsCannotExecuteTheirParsedPrefix(string protocol, bool streaming)
    {
        const string arguments = """{"path":"target.txt","content":"replacement", BROKEN}""";
        await using var server = new ProviderToolArgumentServer(protocol, arguments, streaming);
        var model = new ModelDescriptor("fixture-model", "Fixture", 128000, "configured", Provider: "fixture", Api: protocol);
        var provider = new ProviderProfile("fixture", "Fixture", server.Endpoint, true, false, null, null, [model], Api: protocol);
        using var client = ProviderChatClientFactory.Create(new ModelSelection(provider, model, "fixture-key", true, "fixture"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ChatMessage[] messages = [new(ChatRole.User, "create a document")];
        var response = streaming
            ? await client.GetStreamingResponseAsync(messages, cancellationToken: deadline.Token).ToChatResponseAsync(deadline.Token)
            : await client.GetResponseAsync(messages, cancellationToken: deadline.Token);
        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.NotNull(call.Exception);
        Assert.Empty(call.Arguments!);
        Assert.Contains(arguments, call.Exception.Message);

        var invocations = 0;
        using var replay = new ReplayCallClient(call);
        var agent = new ChatClientAgent(replay, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions
            {
                Tools = [AIFunctionFactory.Create((string path, string content) =>
                {
                    invocations++;
                    return "written";
                }, "fixture_tool")]
            }
        });
        await agent.RunAsync("create a document", cancellationToken: deadline.Token);
        Assert.Equal(0, invocations);
        Assert.NotNull(replay.ToolResult);
    }

    [Theory]
    [InlineData("openai-completions", false)]
    [InlineData("openai-completions", true)]
    [InlineData("openai-responses", false)]
    [InlineData("openai-responses", true)]
    [InlineData("anthropic-messages", true)]
    public async Task MalformedUnicodeEscapesRemainLiteralWithoutDroppingFollowingFields(string protocol, bool streaming)
    {
        const string arguments = """{"path":"a.tex","content":"\usepackage{x} caf\u00e9 \url{x}","mode":"w"}""";
        await using var server = new ProviderToolArgumentServer(protocol, arguments, streaming, requests: 2);
        var model = new ModelDescriptor("fixture-model", "Fixture", 128000, "configured", Provider: "fixture", Api: protocol);
        var provider = new ProviderProfile("fixture", "Fixture", server.Endpoint, true, false, null, null, [model], Api: protocol);
        using var client = ProviderChatClientFactory.Create(new ModelSelection(provider, model, "fixture-key", true, "fixture"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ChatMessage[] messages = [new(ChatRole.User, "create a document")];
        var response = streaming
            ? await client.GetStreamingResponseAsync(messages, cancellationToken: deadline.Token).ToChatResponseAsync(deadline.Token)
            : await client.GetResponseAsync(messages, cancellationToken: deadline.Token);
        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Null(call.Exception);
        var parsed = JsonSerializer.SerializeToElement(call.Arguments);
        Assert.Equal("a.tex", parsed.GetProperty("path").GetString());
        Assert.Equal("\\usepackage{x} café \\url{x}", parsed.GetProperty("content").GetString());
        Assert.Equal("w", parsed.GetProperty("mode").GetString());
        var history = messages.Concat(response.Messages).Append(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call_fixture", "done")])).Append(new ChatMessage(ChatRole.User, "continue"));
        if (streaming)
            _ = await client.GetStreamingResponseAsync(history, cancellationToken: deadline.Token).ToChatResponseAsync(deadline.Token);
        else _ = await client.GetResponseAsync(history, cancellationToken: deadline.Token);
        JsonElement replay;
        if (protocol == "anthropic-messages")
            replay = server.LastRequest.GetProperty("messages").EnumerateArray()
                .SelectMany(message => message.GetProperty("content").EnumerateArray())
                .Single(block => block.GetProperty("type").GetString() == "tool_use").GetProperty("input");
        else
        {
            var json = protocol == "openai-completions"
                ? server.LastRequest.GetProperty("messages").EnumerateArray()
                    .Single(message => message.GetProperty("role").GetString() == "assistant")
                    .GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString()!
                : server.LastRequest.GetProperty("input").EnumerateArray()
                    .Single(item => item.GetProperty("type").GetString() == "function_call").GetProperty("arguments").GetString()!;
            using var document = JsonDocument.Parse(json);
            replay = document.RootElement.Clone();
        }
        Assert.Equal("\\usepackage{x} café \\url{x}", replay.GetProperty("content").GetString());
    }

    private sealed class ReplayCallClient(FunctionCallContent call) : IChatClient
    {
        private int _requests;
        public FunctionResultContent? ToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_requests++ == 0)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            ToolResult = Assert.Single(messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "arguments rejected")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
