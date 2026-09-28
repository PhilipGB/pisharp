using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class AnthropicThinkingSignatureCompatibilityTests
{
    [Theory]
    [InlineData(true, null, "thinking", "")]
    [InlineData(false, null, "text", null)]
    [InlineData(false, "signed", "thinking", "signed")]
    [InlineData(true, " \t ", "thinking", "")]
    public async Task AnthropicHistoryReplayHonorsEmptySignatureCompatibility(
        bool allowEmptySignature, string? signatureValue, string expectedType, string? expectedSignature)
    {
        using var listener = StartListener(out var port);
        string? assistantBlockType = null;
        string? assistantText = null;
        string? assistantSignature = null;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            Assert.Equal("/v1/messages", request.Request.Url?.AbsolutePath);
            using var body = await JsonDocument.ParseAsync(request.Request.InputStream);
            var messages = body.RootElement.GetProperty("messages");
            var assistant = messages.EnumerateArray().Single(message => message.GetProperty("role").GetString() == "assistant");
            var block = Assert.Single(assistant.GetProperty("content").EnumerateArray());
            assistantBlockType = block.GetProperty("type").GetString();
            if (block.TryGetProperty("thinking", out var thinking)) assistantText = thinking.GetString();
            if (block.TryGetProperty("text", out var text)) assistantText = text.GetString();
            if (block.TryGetProperty("signature", out var signature)) assistantSignature = signature.GetString();
            request.Response.ContentType = "application/json";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("""
                {"id":"msg_signature","type":"message","role":"assistant","model":"qwen3.8-flash","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":4,"output_tokens":2}}
                """);
            await writer.FlushAsync();
            request.Response.Close();
        });

        using var compatibilityDocument = JsonDocument.Parse($"{{\"allowEmptySignature\":{allowEmptySignature.ToString().ToLowerInvariant()}}}");
        var model = new ModelDescriptor("qwen3.8-flash", "opencode", 128000, "configured", Reasoning: true,
            Provider: "opencode", Api: "anthropic-messages", Compatibility: compatibilityDocument.RootElement.Clone());
        var profile = new ProviderProfile("opencode", "OpenCode", new Uri($"http://127.0.0.1:{port}"), true,
            false, null, null, [model], Api: "anthropic-messages");
        var client = ProviderChatClientFactory.Create(new ModelSelection(profile, model, "fixture-key", true, "fixture"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reasoning = new TextReasoningContent("internal reasoning");
        if (signatureValue is not null) reasoning.ProtectedData = signatureValue;
        var response = await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "first"),
                new ChatMessage(ChatRole.Assistant, [reasoning]),
                new ChatMessage(ChatRole.User, "second")
            ], new ChatOptions { Reasoning = ThinkingLevels.ToOptions("high") }, deadline.Token);
        await server.WaitAsync(deadline.Token);

        Assert.Equal("ok", response.Text);
        Assert.Equal(expectedType, assistantBlockType);
        Assert.Equal("internal reasoning", assistantText);
        Assert.Equal(expectedSignature, assistantSignature);
    }

    private static HttpListener StartListener(out int port)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }
}
