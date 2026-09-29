using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Extensions;

/// <summary>An image returned to the model by a tool.</summary>
public sealed record PiSharpToolImage(string MimeType, string DataBase64)
{
    public DataContent? ToDataContent()
    {
        if (MimeType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp") ||
            DataBase64.Length > 8 * 1024 * 1024) return null;
        try { return new DataContent(Convert.FromBase64String(DataBase64), MimeType); }
        catch (FormatException) { return null; }
    }
}

/// <summary>A tool outcome with separate model text, presentation details and machine-readable data.</summary>
public sealed record PiSharpToolResult(
    string Text,
    object? Details = null,
    JsonElement? StructuredContent = null,
    bool IsError = false,
    string? Error = null,
    IReadOnlyList<PiSharpToolImage>? Images = null,
    UsageDetails? Usage = null,
    bool Terminate = false)
{
    [JsonPropertyName("piSharpToolResult")]
    public bool Marker => true;

    public override string ToString() => Text;
}
