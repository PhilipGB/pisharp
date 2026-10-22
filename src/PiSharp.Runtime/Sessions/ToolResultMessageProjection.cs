using Microsoft.Extensions.AI;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>Preserves canonical tool results while projecting model text and image attachments.</summary>
internal static class ToolResultMessageProjection
{
    // Request-only attribution lets provider adapters apply their error-result image rules.
    internal const string ErrorImageCountKey = "pisharp.errorToolImageCount";
    internal const string ErrorImageIndexesKey = "pisharp.errorToolImageIndexes";

    public static IReadOnlyList<ChatMessage> Expand(IReadOnlyList<ChatMessage> messages, bool supportsImages,
        bool flattenStructuredResults)
    {
        var expanded = new List<ChatMessage>(messages.Count);
        var index = 0;
        while (index < messages.Count)
        {
            if (messages[index].Role != ChatRole.Tool)
            {
                expanded.Add(messages[index++]);
                continue;
            }

            var attachments = new List<AIContent>();
            var errorImageIndexes = new List<int>();
            while (index < messages.Count && messages[index].Role == ChatRole.Tool)
            {
                var message = messages[index++];
                var contents = new List<AIContent>(message.Contents.Count);
                var replaced = false;
                foreach (var content in message.Contents)
                {
                    if (content is FunctionResultContent result && ReadToolOutput.TryRead(result.Result, out var output))
                    {
                        if (!flattenStructuredResults && output.ImageMimeType is null && output.ImageDataBase64 is null)
                        {
                            contents.Add(content);
                            continue;
                        }
                        replaced = true;
                        var resultText = output.Text;
                        if (output.ImageMimeType is not null || output.ImageDataBase64 is not null)
                        {
                            if (!supportsImages)
                                resultText += "\n[Current model does not support images. The image will be omitted from this request.]";
                            else if (output.TryCreateImageContent(out var image))
                                attachments.Add(image);
                            else
                                resultText += "\n[Image content is unavailable.]";
                        }
                        contents.Add(new FunctionResultContent(result.CallId, resultText)
                        {
                            Exception = result.Exception,
                            AdditionalProperties = result.AdditionalProperties?.Clone()
                        });
                        continue;
                    }

                    if (flattenStructuredResults && content is FunctionResultContent structuredResult &&
                        ToolResultOutput.TryRead(structuredResult.Result, out var structuredText, out _))
                    {
                        replaced = true;
                        var projected = new FunctionResultContent(structuredResult.CallId, structuredText)
                        {
                            Exception = structuredResult.Exception,
                            AdditionalProperties = structuredResult.AdditionalProperties?.Clone()
                        };
                        if (ToolResultOutput.TryReadContract(structuredResult.Result, out var contract))
                        {
                            var images = contract.Images?.Select(image => image.ToDataContent())
                                .OfType<DataContent>().ToArray() ?? [];
                            if (contract.IsError)
                            {
                                projected.AdditionalProperties ??= new();
                                projected.AdditionalProperties[ErrorImageCountKey] = images.Length;
                                errorImageIndexes.AddRange(Enumerable.Range(attachments.Count, images.Length));
                            }
                            attachments.AddRange(images);
                        }
                        contents.Add(projected);
                    }
                    else contents.Add(content);
                }

                expanded.Add(replaced ? new ChatMessage(message.Role, contents) : message);
            }

            if (attachments.Count > 0)
                expanded.Add(new ChatMessage(ChatRole.User,
                    [new TextContent("Attached image(s) from tool result:"), .. attachments])
                {
                    AdditionalProperties = errorImageIndexes.Count == 0 ? null : new()
                    {
                        [ErrorImageIndexesKey] = errorImageIndexes.ToArray()
                    }
                });
        }
        return expanded;
    }

}
