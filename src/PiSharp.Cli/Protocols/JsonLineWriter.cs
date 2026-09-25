using System.Text.Json;

namespace PiSharp.Cli.Protocols;

/// <summary>Atomic LF-framed records for concurrent protocol commands and agent events.</summary>
public sealed class JsonLineWriter(TextWriter output)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task EmitAsync(object value, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(json + '\n');
            await output.FlushAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }
}
