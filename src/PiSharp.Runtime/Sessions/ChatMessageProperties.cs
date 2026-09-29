using System.Text.Json;

namespace PiSharp.Runtime.Sessions;

/// <summary>Reads metadata consistently before and after native session serialization.</summary>
internal static class ChatMessageProperties
{
    public static string? String(IDictionary<string, object?>? properties, string name) =>
        properties?.TryGetValue(name, out var value) == true ? value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        } : null;
}
