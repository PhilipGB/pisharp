using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Tools;
using SkiaSharp;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
if (args.Length > 2 && args[2] == "--sample-images-only")
{
    using var fixture = JsonDocument.Parse(File.ReadAllText(args[0]));
    var sampleResults = new List<object>();
    foreach (var sample in fixture.RootElement.GetProperty("samples").EnumerateArray())
    {
        var asset = fixture.RootElement.GetProperty("assets").GetProperty(sample.GetProperty("asset").GetString()!);
        using var bytes = new MemoryStream(Convert.FromBase64String(asset.GetProperty("base64").GetString()!));
        using var codec = SKCodec.Create(bytes);
        using var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("Sample input could not be decoded.");
        using var resized = ReadImageResampler.Resize(bitmap, sample.GetProperty("width").GetInt32(), sample.GetProperty("height").GetInt32(), CancellationToken.None);
        var pixels = resized.GetPixelSpan().ToArray();
        sampleResults.Add(new { caseId = sample.GetProperty("caseId").GetString(), width = resized.Width, height = resized.Height,
            rgbaBase64 = Convert.ToBase64String(pixels), rgbaSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pixels)) });
    }
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { results = sampleResults }, jsonOptions) + "\n");
    return;
}
if (args.Length > 2 && args[2] == "--decode-images-only")
{
    using var fixture = JsonDocument.Parse(File.ReadAllText(args[0]));
    var decodedResults = new List<object>();
    foreach (var asset in fixture.RootElement.GetProperty("assets").EnumerateObject())
    {
        using var bytes = new MemoryStream(Convert.FromBase64String(asset.Value.GetProperty("base64").GetString()!));
        using var codec = SKCodec.Create(bytes);
        if (codec is null) { decodedResults.Add(new { caseId = asset.Name, unsupported = true }); continue; }
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[checked(info.Width * info.Height * 4)];
        if (codec.GetPixels(info, pixels) != SKCodecResult.Success)
        { decodedResults.Add(new { caseId = asset.Name, unsupported = true }); continue; }
        decodedResults.Add(new { caseId = asset.Name, width = info.Width, height = info.Height,
            rgbaBase64 = Convert.ToBase64String(pixels),
            rgbaSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pixels)) });
    }
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { results = decodedResults }, jsonOptions) + "\n");
    return;
}
var requests = JsonSerializer.Deserialize<List<ProbeRequest>>(File.ReadAllText(args[0]), jsonOptions)!;
var declarations = new AgentToolCatalog(new CodingTools("/tmp"), null, null, false, false, null, null)
    .Registrations.ToDictionary(registration => registration.Function.Name, registration => new
    {
        name = registration.Function.Name,
        description = registration.Function.Description,
        parameters = registration.Function.JsonSchema,
        outputSchema = registration.OutputSchema,
        constrainedSampling = registration.ConstrainedSampling
    });
var results = new List<object>();
foreach (var request in requests)
{
    var updates = new List<object>();
    using var cancellation = new CancellationTokenSource();
    if (request.PreCancelled) cancellation.Cancel();
    var tools = new CodingTools(request.ExecutionCwd ?? request.Workspace, request.Options?.ShellPath,
        request.Options?.ResizeOptions, request.Options?.CommandPrefix);
    if (request.Context is { } context && context.TryGetProperty("model", out var model))
    {
        if (model.TryGetProperty("input", out var inputs))
            tools.SetReadModelSupportsImages(inputs.EnumerateArray().Any(input => input.GetString() == "image"));
        if (model.TryGetProperty("inputLimits", out var limits) && limits.TryGetProperty("images", out var images) &&
            images.TryGetProperty("resize", out var resize))
            tools.SetImageResizeOptions(resize.Deserialize<ModelImageResizeOptions>(jsonOptions));
    }
    var function = tools.CreateAll().OfType<AIFunction>().Single(item => item.Name == request.Tool);
    var arguments = new AIFunctionArguments(request.Arguments.ToDictionary(
        pair => pair.Key, pair => (object?)pair.Value))
    {
        Context = new Dictionary<object, object?>
        {
            [CodingTools.BashSnapshotContextKey] = (Action<BashToolUpdate>)(update =>
            {
                object[] content = update.Text is null ? [] : [new { type = "text", text = update.Text }];
                lock (updates)
                    updates.Add(new { content, details = update.Details });
                if (request.AcknowledgeEmptyUpdate && update.Text == "")
                    File.WriteAllText(Path.Combine(request.Workspace, "ack"), "go\n");
                if (request.CancelOnOutput is not null && update.Text?.Contains(request.CancelOnOutput, StringComparison.Ordinal) == true)
                    cancellation.Cancel();
            })
        }
    };
    object result;
    try
    {
        var value = await function.InvokeAsync(arguments, cancellation.Token);
        var content = new List<object>();
        object? structuredContent = null;
        var isError = false;
        var projected = ToolResultOutput.TryRead(value, out var text, out var details);
        if (ToolResultOutput.TryReadContract(value, out var contract))
        {
            text = contract.Text;
            isError = contract.IsError;
            structuredContent = contract.StructuredContent;
            foreach (var image in contract.Images ?? [])
                content.Add(new { type = "image", data = image.DataBase64, mimeType = image.MimeType });
        }
        else if (ReadToolOutput.TryRead(value, out var read) && read.ImageDataBase64 is not null)
        {
            content.Add(new { type = "image", data = read.ImageDataBase64, mimeType = read.ImageMimeType });
        }
        if (!projected)
            text = value is JsonElement element && element.ValueKind == JsonValueKind.String
                ? element.GetString()! : value?.ToString() ?? "";
        content.Insert(0, new { type = "text", text });
        result = new { request.CaseId, content, details, structuredContent, isError, rejected = false, updates };
    }
    catch (Exception error)
    {
        var text = error.Message;
        result = new { request.CaseId, content = new[] { new { type = "text", text } }, details = (object?)null,
            structuredContent = (object?)null, isError = true, rejected = true, updates };
    }
    results.Add(result);
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { declarations, results }, jsonOptions) + "\n");
}

internal sealed record ProbeRequest(string CaseId, string Tool, string Workspace,
    Dictionary<string, JsonElement> Arguments, bool PreCancelled = false,
    string? CancelOnOutput = null, ProbeOptions? Options = null, JsonElement? Context = null,
    bool AcknowledgeEmptyUpdate = false, string? ExecutionCwd = null);

internal sealed record ProbeOptions(string? ShellPath = null, string? CommandPrefix = null,
    ModelImageResizeOptions? ResizeOptions = null);
