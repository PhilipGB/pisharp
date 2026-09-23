using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

/// <summary>Experimental PiSharp JSONL transport. The event names resemble Pi but the session
/// header and message payloads deliberately identify this as PiSharp, not Pi v3 JSONL.</summary>
public sealed class JsonEventMode(JsonLineWriter output)
{
    public JsonEventMode(TextWriter writer) : this(new JsonLineWriter(writer)) { }

    private Task EmitAsync(object value, CancellationToken cancellationToken) => output.EmitAsync(value, cancellationToken);

    public Task HeaderAsync(ConversationSession conversation, CancellationToken cancellationToken = default) =>
        EmitAsync(new
        {
            type = "session",
            format = "pisharp",
            version = ConversationSession.FormatVersion,
            id = conversation.Id,
            timestamp = DateTimeOffset.UtcNow,
            cwd = conversation.WorkingDirectory
        }, cancellationToken);

    public async Task<bool> RunAsync(ConversationRun run, string prompt, CancellationToken cancellationToken = default)
    {
        var startingCount = run.Conversation.ActiveMessages().Count;
        var text = new System.Text.StringBuilder();
        await EmitAsync(new { type = "agent_start" }, cancellationToken);
        await EmitAsync(new { type = "turn_start" }, cancellationToken);
        var user = new { role = "user", content = prompt, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        await EmitAsync(new { type = "message_start", message = user }, cancellationToken);
        await EmitAsync(new { type = "message_end", message = user }, cancellationToken);
        await EmitAsync(new { type = "message_start", message = new { role = "assistant", content = Array.Empty<string>(), stopReason = "pending" } }, cancellationToken);
        var succeeded = true;
        try
        {
            await foreach (var update in run.RunStreamingAsync(prompt, cancellationToken))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    text.Append(update.Text);
                    await EmitAsync(new
                    {
                        type = "message_update",
                        assistantMessageEvent = new
                        {
                            type = "text_delta",
                            contentIndex = 0,
                            delta = update.Text
                        }
                    }, cancellationToken);
                }
                if (update.Contents is null) continue;
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent call)
                        await EmitAsync(new
                        {
                            type = "tool_execution_start",
                            toolCallId = call.CallId,
                            toolName = call.Name,
                            args = call.Arguments
                        }, cancellationToken);
                    else if (content is FunctionResultContent result)
                        await EmitAsync(new
                        {
                            type = "tool_execution_end",
                            toolCallId = result.CallId,
                            result = new { content = new[] { new { type = "text", text = result.Result?.ToString() ?? result.Exception?.Message ?? "" } } },
                            isError = result.Exception is not null
                        }, cancellationToken);
                }
            }
        }
        catch (Exception error)
        {
            succeeded = false;
            await EmitAsync(new { type = "error", error = error.Message }, CancellationToken.None);
        }
        var added = run.Conversation.ActiveMessages().Skip(startingCount).ToArray();
        var assistant = added.LastOrDefault(message => message.Role == ChatRole.Assistant);
        var final = new
        {
            role = "assistant",
            content = assistant?.Text ?? text.ToString(),
            stopReason = succeeded ? "stop" : "error"
        };
        await EmitAsync(new { type = "message_end", message = final }, CancellationToken.None);
        await EmitAsync(new
        {
            type = "turn_end",
            message = final,
            toolResults = added.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>().Select(result => new
            {
                toolCallId = result.CallId,
                isError = result.Exception is not null,
                text = result.Result?.ToString() ?? result.Exception?.Message
            }).ToArray()
        }, CancellationToken.None);
        await EmitAsync(new { type = "agent_end", messages = new[] { final }, willRetry = false }, CancellationToken.None);
        await EmitAsync(new { type = "agent_settled" }, CancellationToken.None);
        return succeeded;
    }
}
