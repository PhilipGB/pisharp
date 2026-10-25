using System.Text.Json;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Tools;
using SkiaSharp;

using var input = JsonDocument.Parse(args[0]);
var results = new List<object>();
foreach (var scenario in input.RootElement.EnumerateArray())
{
    var bytes = Convert.FromBase64String(scenario.GetProperty("data").GetString()!);
    ModelImageResizeOptions? resize = null;
    if (scenario.TryGetProperty("resize", out var profile) && profile.ValueKind == JsonValueKind.Object)
        resize = new ModelImageResizeOptions(
            profile.TryGetProperty("maxWidth", out var width) ? width.GetInt32() : null,
            profile.TryGetProperty("maxHeight", out var height) ? height.GetInt32() : null,
            profile.TryGetProperty("maxBytes", out var maxBytes) ? maxBytes.GetInt32() : null,
            profile.TryGetProperty("jpegQuality", out var quality) ? quality.GetInt32() : null);
    var result = ReadImageProcessor.Process(bytes, scenario.GetProperty("mimeType").GetString()!,
        resizeOptions: resize,
        autoResizeImages: !scenario.TryGetProperty("autoResizeImages", out var autoResize) || autoResize.GetBoolean());
    var successful = result.ImageDataBase64 is { Length: > 0 };
    int? decodedWidth = null;
    int? decodedHeight = null;
    if (successful && scenario.GetProperty("id").GetString() is not ("gif-pass-through" or "webp-pass-through"))
    {
        using var decoded = SKBitmap.Decode(Convert.FromBase64String(result.ImageDataBase64!));
        if (decoded is not null)
        {
            decodedWidth = decoded.Width;
            decodedHeight = decoded.Height;
        }
    }
    var newline = result.Text.IndexOf('\n');
    var hints = newline < 0 ? Array.Empty<string>() : result.Text[(newline + 1)..]
        .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    results.Add(new
    {
        id = scenario.GetProperty("id").GetString(),
        ok = successful,
        mimeType = result.ImageMimeType,
        data = result.ImageDataBase64,
        width = decodedWidth,
        height = decodedHeight,
        hints,
        message = successful ? null : newline < 0 ? result.Text : result.Text[(newline + 1)..]
    });
}
Console.WriteLine(JsonSerializer.Serialize(results));
