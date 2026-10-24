using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Runtime.Sessions;

internal sealed class LifecycleStructuredContentConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonElement.ParseValue(ref reader);

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "image")
        {
            value.WriteTo(writer);
            return;
        }
        // Lifecycle JSON keeps image bytes in process, including the structured representation.
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject())
            if (property.Name != "data") property.WriteTo(writer);
        writer.WriteEndObject();
    }
}
