using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace PiSharp.Tests;

internal sealed class JsonLineProcessOutput
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });
    private readonly Task _drain;

    public JsonLineProcessOutput(Process process) => _drain = DrainAsync(process);

    public Task Completion => _drain;

    public async Task<JsonDocument> ReadResponseAsync(string id, CancellationToken cancellationToken)
    {
        await foreach (var line in _lines.Reader.ReadAllAsync(cancellationToken))
        {
            using var record = JsonDocument.Parse(line);
            var root = record.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.String ||
                responseId.GetString() != id || !root.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String || type.GetString() != "response")
                continue;
            return JsonDocument.Parse(line);
        }
        throw new EndOfStreamException($"RPC process closed before response {id}.");
    }

    private async Task DrainAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                await _lines.Writer.WriteAsync(line);
            _lines.Writer.TryComplete();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            _lines.Writer.TryComplete(error);
        }
    }
}
