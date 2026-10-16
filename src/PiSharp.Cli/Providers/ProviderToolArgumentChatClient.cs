using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ChatToolCall = OpenAI.Chat.ChatToolCall;
using OpenAI.Responses;
using PiSharp.Core;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

/// <summary>Repairs SDK tool argument parsing before the native tool call reaches MAF.</summary>
internal sealed class ProviderToolArgumentChatClient(IChatClient inner, ProviderToolCallDeltaCapture? capture = null)
    : DelegatingChatClient(inner), IProviderToolCallDeltaSource
{
    public IProviderToolCallDeltaCapture? BeginToolCallDeltaCapture() =>
        capture?.BeginToolCallDeltaCapture() ?? (InnerClient as IProviderToolCallDeltaSource)?.BeginToolCallDeltaCapture();

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages) Repair(message.Contents, message.RawRepresentation);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var ownedCapture = capture?.EnsureCapture();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            Repair(update.Contents, update.RawRepresentation);
            yield return update;
        }
    }

    private void Repair(IList<AIContent> contents, object? raw)
    {
        foreach (var call in contents.OfType<FunctionCallContent>())
        {
#pragma warning disable OPENAI001
            var arguments = capture?.Arguments(call.CallId) ?? (call.RawRepresentation switch
            {
                ChatToolCall chat => chat.FunctionArguments.ToString(),
                FunctionCallResponseItem response => response.FunctionArguments.ToString(),
                _ => raw is StreamingResponseOutputItemDoneUpdate { Item: FunctionCallResponseItem response }
                    ? response.FunctionArguments.ToString() : null
            });
#pragma warning restore OPENAI001
            if (arguments is null) continue;
            Normalize(call, arguments);
        }
    }

    internal static void Normalize(FunctionCallContent call, string arguments)
    {
        // Partial prefixes are useful for progress, but cannot authorize a completed tool invocation.
        try
        {
            var parsed = ToolArgumentJson.ParseComplete(arguments);
            call.Arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(parsed.GetRawText());
            call.Exception = null;
        }
        catch (JsonException error)
        {
            call.Arguments = new Dictionary<string, object?>();
            call.Exception = new JsonException($"{error.Message}\n\nReceived arguments:\n{arguments}", error);
        }
        call.RawRepresentation = null;
    }
}
