using System.Text.Json;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace PiSharp.Cli.Protocols;

/// <summary>Atomic LF-framed records for concurrent protocol commands and agent events.</summary>
public sealed class JsonLineWriter(TextWriter output)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly ConcurrentDictionary<Type, Func<object, string?>> s_recordTypes = new();

    public async Task EmitAsync(object value, CancellationToken cancellationToken = default)
    {
        var json = Serialize(value);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(json + '\n');
            await output.FlushAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static string Serialize(object value)
    {
        var getType = s_recordTypes.GetOrAdd(value.GetType(), static type =>
        {
            var property = type.GetProperty("type");
            return property is null ? static _ => null : record => property.GetValue(record) as string;
        });
        if (getType(value) != "response") return JsonSerializer.Serialize(value);

        var response = JsonSerializer.SerializeToNode(value)?.AsObject() ?? new JsonObject();
        if (response["id"] is null) response.Remove("id");
        if (response["error"] is null) response.Remove("error");
        return response.ToJsonString();
    }
}
