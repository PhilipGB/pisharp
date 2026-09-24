using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class ReadImageToolTests
{
    [Fact]
    public void ImageSniffingMatchesPinnedSupportedFormatBoundaries()
    {
        Assert.Equal("image/jpeg", ReadImageDetector.Detect([0xff, 0xd8, 0xff, 0xe0]));
        Assert.Null(ReadImageDetector.Detect([0xff, 0xd8, 0xff, 0xf7]));
        Assert.Equal("image/png", ReadImageDetector.Detect(PngHeader(animated: false)));
        Assert.Null(ReadImageDetector.Detect(PngHeader(animated: true)));
        Assert.Equal("image/gif", ReadImageDetector.Detect(Encoding.ASCII.GetBytes("GIF89a")));
        Assert.Equal("image/webp", ReadImageDetector.Detect(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBP")));
        Assert.Equal("image/bmp", ReadImageDetector.Detect(BmpHeader()));
        Assert.Null(ReadImageDetector.Detect(Encoding.ASCII.GetBytes("not an image")));
    }

    [Fact]
    public void SmallPngCanBeDecodedAndProcessedForInlineUse()
    {
        var bytes = CreatePng(1, 1);
        using var decoded = SKBitmap.Decode(bytes);
        Assert.NotNull(decoded);
        Assert.Equal((1, 1), (decoded!.Width, decoded.Height));
        var output = ReadImageProcessor.Process(bytes, "image/png");
        Assert.NotNull(output.ImageDataBase64);
        Assert.Equal("Read image file [image/png]", output.Text);
    }

    [Theory]
    [InlineData("openai-responses")]
    [InlineData("openai-completions")]
    public async Task ReadToolImageReachesOpenAiProviderAndCanBeReplayedAfterReload(string api)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-read-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = CreatePng(1, 1);
            await File.WriteAllBytesAsync(Path.Combine(root, "artwork.bin"), image);
            var largeImage = CreateHighEntropyPng(1200, 1200);
            Assert.True(largeImage.Length < 20 * 1024 * 1024);
            Assert.True(Base64Length(largeImage.Length) >= ReadImageProcessor.MaxBase64Bytes);
            await File.WriteAllBytesAsync(Path.Combine(root, "large.png"), largeImage);

            using var listener = new HttpListener();
            var port = 0;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                using var reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
                listener.Prefixes.Clear();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try { listener.Start(); break; }
                catch (HttpListenerException) when (attempt < 9) { }
            }

            var requests = new List<JsonDocument>();
            var server = Task.Run(async () =>
            {
                for (var index = 0; index < 7; index++)
                {
                    var request = await listener.GetContextAsync();
                    var expectedPath = api == "openai-responses" ? "/v1/responses" : "/v1/chat/completions";
                    Assert.Equal(expectedPath, request.Request.Url?.AbsolutePath);
                    using var reader = new StreamReader(request.Request.InputStream);
                    requests.Add(JsonDocument.Parse(await reader.ReadToEndAsync()));

                    request.Response.ContentType = "text/event-stream";
                    await using var writer = new StreamWriter(request.Response.OutputStream);
                    if (api == "openai-responses")
                    {
                        await WriteResponsesEventsAsync(writer, index);
                    }
                    else
                        await WriteCompletionsEventsAsync(writer, index);
                    await writer.WriteAsync("data: [DONE]\n\n");
                    await writer.FlushAsync();
                    request.Response.Close();
                }
            });

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var endpoint = $"http://127.0.0.1:{port}/v1";
            var selection = new ModelSelection(
                new ProviderProfile("fixture", "Fixture", new Uri(endpoint), true, false, "FIXTURE_API_KEY", null, []),
                new ModelDescriptor("fixture-model", "fixture", null, "fixture", Provider: "fixture", Api: api,
                    Input: ["text", "image"]),
                "fixture-key", false, "test");
            var store = new ConversationStore(root, Path.Combine(root, "sessions"));
            var conversation = new ConversationSession(root, "fixture-model", endpoint, "fixture");
            var path = store.NewPath(conversation);
            var run = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(selection), new CodingTools(root)),
                conversation, save: token => store.SaveAsync(conversation, path, token));
            var firstTurn = "";
            await foreach (var update in run.RunStreamingAsync("inspect artwork", deadline.Token)) firstTurn += update.Text;
            Assert.Equal("seen 1", firstTurn);
            await store.SaveAsync(conversation, path, deadline.Token);

            var loaded = await store.LoadAsync(path, deadline.Token);
            var savedMessages = loaded.ActiveMessages();
            var storedResult = savedMessages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Single(result => result.CallId == "read_1");
            var storedText = storedResult.Result is JsonElement storedJson && storedJson.ValueKind == JsonValueKind.String
                ? storedJson.GetString() : storedResult.Result as string;
            Assert.Equal("Read image file [image/png]", storedText);
            Assert.Equal(image, savedMessages.SelectMany(message => message.Contents).OfType<DataContent>().Single().Data.ToArray());
            var resumed = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(selection), new CodingTools(root)), loaded);
            var secondTurn = "";
            await foreach (var update in resumed.RunStreamingAsync("describe the image again", deadline.Token)) secondTurn += update.Text;
            Assert.Equal("seen 2", secondTurn);

            var textOnlySelection = selection with { Model = selection.Model with { Input = ["text"] } };
            var textOnlyRun = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(textOnlySelection),
                new CodingTools(root), supportsImages: false), loaded);
            var thirdTurn = "";
            await foreach (var update in textOnlyRun.RunStreamingAsync("continue with a text only model", deadline.Token)) thirdTurn += update.Text;
            Assert.Equal("seen 3", thirdTurn);

            var blockedRun = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(selection),
                new CodingTools(root), blockImages: true), loaded);
            var fourthTurn = "";
            await foreach (var update in blockedRun.RunStreamingAsync("continue with images blocked", deadline.Token)) fourthTurn += update.Text;
            Assert.Equal("seen 4", fourthTurn);

            var largeAgent = new PiAgent(ProviderChatClientFactory.Create(selection), new CodingTools(root));
            var largeSession = await largeAgent.CreateSessionAsync(deadline.Token);
            var largeTurn = "";
            await foreach (var update in largeAgent.RunStreamingAsync("inspect the large image", largeSession, deadline.Token))
                largeTurn += update.Text;
            Assert.Equal("seen 6", largeTurn);

            await server.WaitAsync(deadline.Token);
            Assert.Equal(7, requests.Count);
            AssertResponseContainsImage(requests[1].RootElement, image);
            AssertResponseContainsImage(requests[2].RootElement, image);
            var textOnlyBody = requests[3].RootElement.GetRawText();
            Assert.DoesNotContain("input_image", textOnlyBody, StringComparison.Ordinal);
            Assert.DoesNotContain("image_url", textOnlyBody, StringComparison.Ordinal);
            Assert.Contains("Current model does not support images. The image will be omitted from this request.", textOnlyBody, StringComparison.Ordinal);
            var blockedBody = requests[4].RootElement.GetRawText();
            Assert.DoesNotContain("input_image", blockedBody, StringComparison.Ordinal);
            Assert.DoesNotContain("image_url", blockedBody, StringComparison.Ordinal);
            Assert.Contains("Image reading is disabled.", blockedBody, StringComparison.Ordinal);
            AssertResponseImageBelowLimit(requests[6].RootElement);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task WriteResponsesEventsAsync(StreamWriter writer, int index)
    {
        await writer.WriteAsync($"data: {{\"type\":\"response.created\",\"response\":{{\"id\":\"resp_{index}\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}}}\n\n");
        if (index is 0 or 5)
        {
            var (itemId, callId, path) = index == 0 ? ("fc_1", "read_1", "artwork.bin") : ("fc_large", "read_large", "large.png");
            var arguments = JsonSerializer.Serialize(new { path, offset = 0, limit = 0 });
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(new { type = "response.output_item.added", output_index = 0, item = new { id = itemId, type = "function_call", call_id = callId, name = "read", arguments = "" } })}\n\n");
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(new { type = "response.function_call_arguments.done", output_index = 0, item_id = itemId, arguments })}\n\n");
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 0, item = new { id = itemId, type = "function_call", call_id = callId, name = "read", arguments } })}\n\n");
        }
        else
            await writer.WriteAsync($"data: {{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_{index}\",\"output_index\":0,\"content_index\":0,\"delta\":\"seen {index}\"}}\n\n");
        await writer.WriteAsync($"data: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"resp_{index}\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}}}\n\n");
    }

    private static async Task WriteCompletionsEventsAsync(StreamWriter writer, int index)
    {
        if (index is 0 or 5)
        {
            var callId = index == 0 ? "read_1" : "read_large";
            var path = index == 0 ? "artwork.bin" : "large.png";
            var arguments = JsonSerializer.Serialize(new { path, offset = 0, limit = 0 });
            var eventId = $"chatcmpl_{index}";
            var toolCall = new { index = 0, id = callId, type = "function", function = new { name = "read", arguments } };
            var toolChunk = new
            {
                id = eventId,
                @object = "chat.completion.chunk",
                created = 1,
                model = "fixture-model",
                choices = new[] { new { index = 0, delta = new { role = "assistant", tool_calls = new[] { toolCall } }, finish_reason = (string?)null } }
            };
            var completeChunk = new
            {
                id = eventId,
                @object = "chat.completion.chunk",
                created = 1,
                model = "fixture-model",
                choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } }
            };
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(toolChunk)}\n\n");
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(completeChunk)}\n\n");
        }
        else
        {
            await writer.WriteAsync($"data: {{\"id\":\"chatcmpl_{index}\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"assistant\",\"content\":\"seen {index}\"}},\"finish_reason\":null}}]}}\n\n");
            await writer.WriteAsync($"data: {{\"id\":\"chatcmpl_{index}\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}]}}\n\n");
        }
    }

    private static void AssertResponseContainsImage(JsonElement body, byte[] image)
    {
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(image), GetResponseImageDataUrl(body));
    }

    private static void AssertResponseImageBelowLimit(JsonElement body)
    {
        var dataUrl = GetResponseImageDataUrl(body);
        var separator = dataUrl.IndexOf(',');
        Assert.True(separator > 0);
        Assert.StartsWith("data:image/jpeg;base64,", dataUrl, StringComparison.Ordinal);
        var encoded = dataUrl[(separator + 1)..];
        Assert.True(encoded.Length < ReadImageProcessor.MaxBase64Bytes);
        using var decoded = SKBitmap.Decode(Convert.FromBase64String(encoded));
        Assert.NotNull(decoded);
        Assert.Equal((1200, 1200), (decoded!.Width, decoded.Height));
    }

    private static string GetResponseImageDataUrl(JsonElement body)
    {
        string? dataUrl;
        if (body.TryGetProperty("input", out var input))
            dataUrl = input.EnumerateArray()
                .Where(item => item.TryGetProperty("content", out _))
                .SelectMany(item => item.GetProperty("content").EnumerateArray())
                .Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "input_image")
                .Select(part => part.GetProperty("image_url").GetString())
                .Single(value => value is not null);
        else
            dataUrl = body.GetProperty("messages").EnumerateArray()
                .Where(item => item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                .SelectMany(item => item.GetProperty("content").EnumerateArray())
                .Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "image_url")
                .Select(part => part.GetProperty("image_url").GetProperty("url").GetString())
                .Single(value => value is not null);
        return Assert.IsType<string>(dataUrl);
    }

    private static byte[] PngHeader(bool animated)
    {
        var bytes = new byte[animated ? 41 : 33];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        if (animated)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(33), 8);
            Encoding.ASCII.GetBytes("acTL").CopyTo(bytes, 37);
        }
        return bytes;
    }

    private static byte[] BmpHeader()
    {
        var bytes = new byte[54];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
        return bytes;
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static long Base64Length(int byteLength) => ((long)byteLength + 2) / 3 * 4;

    private static byte[] CreateHighEntropyPng(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[checked(width * height * 4)];
        new Random(1729).NextBytes(pixels);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new SKBitmap();
            Assert.True(bitmap.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes));
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        finally { handle.Free(); }
    }
}
