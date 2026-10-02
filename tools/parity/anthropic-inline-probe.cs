using System.Text.Json;
using PiSharp.Tests;
using PiSharp.Runtime.Sessions;
using Microsoft.Extensions.AI;

using var input = JsonDocument.Parse(args[0]);
var results = new List<object>();
foreach (var scenario in input.RootElement.EnumerateArray())
{
    var session = AnthropicInlineToolTests.Import(string.Join('\n', scenario.GetProperty("messages").EnumerateArray().Select(message => message.GetRawText())));
    var (payload, beta) = await AnthropicInlineToolTests.Capture(session.ContextMessages(),
        scenario.GetProperty("systemSupport").GetBoolean(), scenario.GetProperty("toolSupport").GetBoolean(),
        scenario.GetProperty("streaming").GetBoolean(), oauth: scenario.TryGetProperty("oauth", out var oauth) && oauth.GetBoolean());
    results.Add(new { id = scenario.GetProperty("id").GetString(), payload, betas = beta.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).ToArray() });
}
Console.WriteLine(JsonSerializer.Serialize(results));

var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
conversation.Append(new ChatMessage(ChatRole.User, "first"));
conversation.Append(new ChatMessage(ChatRole.Assistant, "reply one"));
conversation.Append(new ChatMessage(ChatRole.User, "second"));
conversation.Append(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("tool2", "read", new Dictionary<string, object?>())]));
conversation.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("tool2", "read output")]));
conversation.Append(new ChatMessage(ChatRole.User, "third"));
var messages = conversation.ContextMessages();
var result = new {
    tokens = messages.Select(message => ConversationCompactionMetadata.EstimateTokens([message])),
    cuts = new[] { 9, 10 }.Select(budget => {
        var plan = conversation.PrepareCompaction(budget)!;
        return new { budget, firstKeptIndex = conversation.Tree.ActivePath().ToList().FindIndex(entry => entry.Id == plan.FirstKeptEntryId), historyCount = plan.MessagesToSummarize.Count };
    })
};
File.WriteAllText(Environment.GetEnvironmentVariable("PISHARP_COMPACTION_OUTPUT")!, JsonSerializer.Serialize(result));
