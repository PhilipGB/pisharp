using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Transform;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli;

/// <summary>Installs wire hooks after Bedrock request marshalling and before SigV4 signing.</summary>
internal static class BedrockProviderWirePipelineCustomizer
{
    internal static void Install(AmazonBedrockRuntimeClient client)
    {
        var property = typeof(AmazonServiceClient).GetProperty("RuntimePipeline",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The AWS runtime pipeline is unavailable for Bedrock wire hooks.");
        var pipeline = property.GetValue(client) as RuntimePipeline ??
            throw new InvalidOperationException("The AWS runtime pipeline is unavailable for Bedrock wire hooks.");
        pipeline.AddHandlerBefore<ChecksumHandler>(new BedrockProviderWirePipelineHandler());
    }
}

internal sealed class BedrockProviderWirePipelineHandler : PipelineHandler
{
    private const string Protocol = "bedrock-converse-stream";
    private const string Provider = "amazon-bedrock";
    private static readonly HashSet<string> s_reservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "host"
    };

    public override async Task<T> InvokeAsync<T>(IExecutionContext executionContext)
    {
        if (executionContext.RequestContext.OriginalRequest is not ConverseStreamRequest request ||
            ProviderWireInvocation.Current is not { } invocation)
            return await base.InvokeAsync<T>(executionContext).ConfigureAwait(false);

        var modelId = request.ModelId ?? "";
        await TransformRequestAsync(executionContext.RequestContext.Request, invocation, modelId,
            executionContext.RequestContext.CancellationToken).ConfigureAwait(false);
        T result;
        try
        {
            result = await base.InvokeAsync<T>(executionContext).ConfigureAwait(false);
        }
        finally
        {
            if (executionContext.ResponseContext.HttpResponse is { } response)
                await invocation.ObserveResponseAsync(Provider, modelId, Protocol, (int)response.StatusCode,
                    ReadHeaders(response), executionContext.RequestContext.CancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private static async Task TransformRequestAsync(IRequest wireRequest, ProviderWireInvocation invocation,
        string modelId, CancellationToken cancellationToken)
    {
        byte[]? serializedContent = null;
        if (wireRequest.ContentStream is { } contentStream)
        {
            if (contentStream.CanSeek) contentStream.Position = 0;
            using var buffer = new MemoryStream();
            await contentStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            serializedContent = buffer.ToArray();
            if (contentStream.CanSeek) contentStream.Position = 0;
        }
        else if (wireRequest.Content is { Length: > 0 } content)
            serializedContent = content;

        if (serializedContent is { Length: > 0 })
        {
            using var document = JsonDocument.Parse(serializedContent);
            var payload = await invocation.TransformPayloadAsync(Provider, modelId, Protocol,
                document.RootElement, cancellationToken).ConfigureAwait(false);
            if (payload.ValueKind != JsonValueKind.Undefined)
            {
                if (payload.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Bedrock ConverseStream payload replacement must be a JSON object.");
                wireRequest.Content = Encoding.UTF8.GetBytes(payload.GetRawText());
                wireRequest.ContentStream = null;
                wireRequest.PrecomputedContentSha256 = null;
            }
        }

        var before = new Dictionary<string, string?>(wireRequest.Headers, StringComparer.OrdinalIgnoreCase);
        var transformed = await invocation.TransformHeadersAsync(Provider, modelId, Protocol, before,
            cancellationToken).ConfigureAwait(false);
        foreach (var key in wireRequest.Headers.Keys.ToArray())
            if (!transformed.ContainsKey(key) && !IsReservedHeader(key)) wireRequest.Headers.Remove(key);

        foreach (var (name, value) in transformed)
        {
            if (IsReservedHeader(name)) continue;
            if (value is null) wireRequest.Headers.Remove(name);
            else wireRequest.Headers[name] = value;
        }
    }

    private static Dictionary<string, string> ReadHeaders(IWebResponseData response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in response.GetHeaderNames())
            headers[name] = response.GetHeaderValue(name);
        return headers;
    }

    private static bool IsReservedHeader(string name) => name.StartsWith("x-amz-",
        StringComparison.OrdinalIgnoreCase) || s_reservedHeaders.Contains(name);
}
