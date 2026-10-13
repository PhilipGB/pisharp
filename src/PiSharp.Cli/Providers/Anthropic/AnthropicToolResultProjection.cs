using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Cli;

/// <summary>Applies Anthropic's text-only error-result rule to attributed tool images.</summary>
internal static class AnthropicToolResultProjection
{
    internal const string ImageOmissionNote = "[image omitted: error tool results cannot contain images]";

    public static IEnumerable<ChatMessage> Project(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Tool)
            {
                var changed = false;
                var contents = message.Contents.Select(content =>
                {
                    if (content is not FunctionResultContent result ||
                        result.AdditionalProperties?.TryGetValue(ToolResultMessageProjection.ErrorImageCountKey,
                            out var value) != true || value is not int count) return content;
                    changed = true;
                    var text = result.Result as string ?? result.Result?.ToString() ?? "";
                    if (count > 0)
                        text = string.Join('\n', new[] { text }.Concat(Enumerable.Repeat(ImageOmissionNote, count)));
                    return new FunctionResultContent(result.CallId, text)
                    {
                        Exception = result.Exception ?? new ToolFailureException("Tool returned an error result.")
                    };
                }).ToList();
                if (changed)
                {
                    var copy = message.Clone();
                    copy.Contents = contents;
                    yield return copy;
                    continue;
                }
            }
            if (message.Role == ChatRole.User &&
                message.AdditionalProperties?.TryGetValue(ToolResultMessageProjection.ErrorImageIndexesKey,
                    out var indexes) == true && indexes is int[] excluded)
            {
                var contents = message.Contents.Where((_, index) => index == 0 || !excluded.Contains(index - 1)).ToList();
                if (contents.Count == 1) continue;
                var copy = message.Clone();
                copy.Contents = contents;
                yield return copy;
                continue;
            }
            yield return message;
        }
    }
}
