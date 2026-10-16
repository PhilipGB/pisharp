using System.ClientModel.Primitives;

namespace PiSharp.Cli;

/// <summary>Composes OpenAI's native SDK transport with tool schema compatibility and raw deltas.</summary>
internal sealed class OpenAiToolCallDeltaCapture
{
    public ProviderToolCallDeltaCapture Capture { get; } = new();
    public PipelineTransport Transport { get; }

    public OpenAiToolCallDeltaCapture(HttpMessageHandler? innerHandler = null)
    {
        var compatibleHandler = new OpenAiToolSchemaCompatibilityHandler(
            innerHandler ?? new ProviderWireActivityHandler(new HttpClientHandler()));
        Transport = new HttpClientPipelineTransport(
            new HttpClient(Capture.CreateHandler(compatibleHandler), disposeHandler: true));
    }
}
