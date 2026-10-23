using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Tools;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class BuiltinReadImageParityTests
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static IEnumerable<object[]> Cases()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "parity",
            "fixtures", "builtin-coding-tool-read-image-results-2026-10-22.json")));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("caseId").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ImageReadMatchesPinnedPayloadPropertiesAndStructuredOutput(string caseId, string fixture)
    {
        using var document = JsonDocument.Parse(fixture);
        var setup = document.RootElement.GetProperty("setup");
        var root = Path.Combine(Path.GetTempPath(), "pisharp-read-image-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in setup.GetProperty("files").EnumerateObject())
                await File.WriteAllBytesAsync(Path.Combine(root, file.Name), Convert.FromBase64String(file.Value.GetProperty("base64").GetString()!));
            var profile = setup.TryGetProperty("options", out var options)
                ? options.GetProperty("resizeOptions").Deserialize<ModelImageResizeOptions>(s_json) : null;
            var function = new CodingTools(root, imageResizeOptions: profile).CreateAll().OfType<AIFunction>().Single(item => item.Name == "read");
            var arguments = new AIFunctionArguments(setup.GetProperty("arguments").EnumerateObject()
                .ToDictionary(item => item.Name, item => (object?)item.Value.Clone()));

            var value = await function.InvokeAsync(arguments);

            Assert.True(ToolResultOutput.TryReadContract(value, out var result), caseId);
            var blocks = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = result.Text });
            foreach (var image in result.Images ?? [])
                blocks.Add(new JsonObject { ["type"] = "image", ["mimeType"] = image.MimeType, ["data"] = image.DataBase64 });
            var actual = new JsonObject
            {
                ["content"] = blocks,
                ["details"] = JsonSerializer.SerializeToNode(result.Details, s_json),
                ["structuredContent"] = JsonSerializer.SerializeToNode(result.StructuredContent, s_json),
                ["isError"] = result.IsError,
                ["rejected"] = false
            };
            foreach (var image in result.Images ?? [])
                Assert.True(image.DataBase64.Length < (profile?.MaxBytes ?? ReadImageProcessor.MaxBase64Bytes));
            if (document.RootElement.GetProperty("imagePolicy").GetString() == "decoded-rgba")
            {
                foreach (var block in blocks.OfType<JsonObject>().Where(item => item["type"]?.GetValue<string>() == "image"))
                    block["data"] = Decode(block["data"]!.GetValue<string>());
                if (actual["structuredContent"] is JsonObject structured && structured["type"]?.GetValue<string>() == "image")
                    structured["data"] = Decode(structured["data"]!.GetValue<string>());
            }
            Assert.True(JsonElement.DeepEquals(document.RootElement.GetProperty("expected"), JsonSerializer.SerializeToElement(actual)),
                caseId + " actual: " + actual.ToJsonString());
            foreach (var file in setup.GetProperty("files").EnumerateObject())
                Assert.Equal(Convert.FromBase64String(file.Value.GetProperty("base64").GetString()!), await File.ReadAllBytesAsync(Path.Combine(root, file.Name)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static JsonNode Decode(string data)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(data));
        using var codec = SKCodec.Create(stream);
        Assert.NotNull(codec);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[checked(info.Width * info.Height * 4)];
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, pixels));
        return JsonSerializer.SerializeToNode(new
        {
            width = info.Width,
            height = info.Height,
            rgbaSha256 = Convert.ToHexStringLower(SHA256.HashData(pixels))
        })!;
    }
}
