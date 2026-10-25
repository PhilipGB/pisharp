using System.Text.Json;
using PiSharp.Cli;
using SkiaSharp;

var root = args[0];
using var input = JsonDocument.Parse(args[1]);
var results = new List<object>();
foreach (var scenario in input.RootElement.EnumerateArray())
{
    var files = scenario.GetProperty("files").EnumerateArray().Select(item => item.GetString()!).ToArray();
    var processed = await CliFileArguments.ProcessFilesAsync(scenario.GetProperty("prompt").GetString()!, files, root);
    var images = new List<object>();
    foreach (var image in processed.Images)
    {
        using var bitmap = SKBitmap.Decode(image.Data.ToArray());
        images.Add(new
        {
            mimeType = image.MediaType,
            data = Convert.ToBase64String(image.Data.ToArray()),
            width = bitmap?.Width,
            height = bitmap?.Height
        });
    }
    results.Add(new { id = scenario.GetProperty("id").GetString(), text = processed.Text, images });
}
Console.WriteLine(JsonSerializer.Serialize(results));
