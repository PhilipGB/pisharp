using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class MultimodalRuntimeTests
{
    [Fact]
    public async Task PromptImagesUseTheSelectedModelResizeProfileAndStoreNormalizedCanonicalBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-prompt-image-limits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var imageBytes = CreatePng(80, 40);
            var client = new ImageAwareClient();
            var profile = new ModelImageResizeOptions(MaxWidth: 12, MaxHeight: 20, MaxBytes: 100_000, JpegQuality: 80);
            var conversation = new ConversationSession(root, "fixture", null);
            var agent = new PiAgent(client, new CodingTools(root, imageResizeOptions: profile), noTools: true);
            var run = await ConversationRun.OpenAsync(agent, conversation);
            var prompt = new ChatMessage(ChatRole.User,
                [new TextContent("inspect"), new DataContent(imageBytes, "image/png")]);

            await foreach (var _ in run.RunUserMessageEventsAsync(prompt, "inspect")) { }

            using var requestImage = SKBitmap.Decode(client.RequestImages.Single().Bytes);
            Assert.Equal((12, 6), (requestImage!.Width, requestImage.Height));
            var canonicalImage = Assert.Single(conversation.ActiveMessages().Single(message => message.Role == ChatRole.User)
                .Contents.OfType<DataContent>());
            using var canonicalBitmap = SKBitmap.Decode(canonicalImage.Data.ToArray());
            Assert.Equal((12, 6), (canonicalBitmap!.Width, canonicalBitmap.Height));

            agent.SetModelRuntime(client, supportsImages: true,
                imageResizeOptions: new ModelImageResizeOptions(MaxWidth: 6, MaxHeight: 20, MaxBytes: 100_000, JpegQuality: 80));
            await foreach (var _ in run.RunUserMessageEventsAsync(new ChatMessage(ChatRole.User, "continue"), "continue")) { }
            using var switchedImage = SKBitmap.Decode(client.RequestImages[1].Bytes);
            Assert.Equal((6, 3), (switchedImage!.Width, switchedImage.Height));
            var storedImages = conversation.ActiveMessages().Where(message => message.Role == ChatRole.User)
                .SelectMany(message => message.Contents.OfType<DataContent>()).ToArray();
            using var storedBitmap = SKBitmap.Decode(storedImages[0].Data.ToArray());
            Assert.Equal((12, 6), (storedBitmap!.Width, storedBitmap.Height));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BeforeAgentStartModelSelectionControlsCanonicalPromptImageNormalization()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-before-start-image-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = CreatePng(80, 40);
            var client = new ImageAwareClient();
            var profile = new ModelImageResizeOptions(MaxWidth: 10, MaxHeight: 20, MaxBytes: 100_000, JpegQuality: 80);
            PiAgent? agent = null;
            var registration = new PiSharp.Runtime.Extensions.ExtensionRegistration();
            registration.AddBeforeAgentStartHandler((_, _, _) =>
            {
                agent!.SetModelRuntime(client, supportsImages: true, imageResizeOptions: profile);
                return ValueTask.FromResult<PiSharp.Runtime.Extensions.PiSharpBeforeAgentStartResult?>(null);
            });
            agent = new PiAgent(client, new CodingTools(root), noTools: true,
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers);
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(agent, conversation);

            await foreach (var _ in run.RunEventsAsync(new ChatMessage(ChatRole.User,
                [new TextContent("inspect"), new DataContent(original, "image/png")]), "inspect")) { }

            using var stored = SKBitmap.Decode(Assert.Single(conversation.ActiveMessages()
                .Single(message => message.Role == ChatRole.User).Contents.OfType<DataContent>()).Data.ToArray());
            Assert.Equal((10, 5), (stored!.Width, stored.Height));
            using var requested = SKBitmap.Decode(client.RequestImages.Single().Bytes);
            Assert.Equal((10, 5), (requested!.Width, requested.Height));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InvalidPromptImageIsOmittedFromProviderRequestWithAnExplicitNote()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-invalid-prompt-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var invalidBytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
            var client = new ImageAwareClient();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noTools: true), conversation);

            await foreach (var _ in run.RunUserMessageEventsAsync(new ChatMessage(ChatRole.User,
                [new TextContent("inspect"), new DataContent(invalidBytes, "image/png")]), "inspect")) { }

            Assert.Empty(client.RequestImages);
            Assert.Contains(client.RequestContents.Single().OfType<TextContent>(), text =>
                text.Text.Contains("[Image omitted: could not be resized below the inline image size limit.]", StringComparison.Ordinal));
            var stored = conversation.ActiveMessages().Single(message => message.Role == ChatRole.User);
            Assert.Empty(stored.Contents.OfType<DataContent>());
            Assert.Contains(stored.Contents.OfType<TextContent>(), text =>
                text.Text.Contains("[Image omitted: could not be resized below the inline image size limit.]", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TextOnlyModelReceivesResizeDiagnosticsBeforeImageCapabilityNotice()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-text-only-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = CreatePng(80, 40);
            var profile = new ModelImageResizeOptions(MaxWidth: 12, MaxHeight: 20, MaxBytes: 100_000, JpegQuality: 80);
            var client = new ImageAwareClient();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client,
                new CodingTools(root, imageResizeOptions: profile), noTools: true, supportsImages: false), conversation);

            await foreach (var _ in run.RunUserMessageEventsAsync(new ChatMessage(ChatRole.User,
                [new TextContent("describe"), new DataContent(original, "image/png")]), "describe")) { }

            Assert.Empty(client.RequestImages);
            var text = string.Join('\n', client.RequestContents.Single().OfType<TextContent>().Select(content => content.Text));
            Assert.Contains("original 80x40, displayed at 12x6", text, StringComparison.Ordinal);
            Assert.Contains("Current model does not support images", text, StringComparison.Ordinal);
            var stored = Assert.Single(conversation.ActiveMessages().Single(message => message.Role == ChatRole.User)
                .Contents.OfType<DataContent>());
            using var storedBitmap = SKBitmap.Decode(stored.Data.ToArray());
            Assert.Equal((12, 6), (storedBitmap!.Width, storedBitmap.Height));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BmpPromptImageIsConvertedBeforeProviderSerialization()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bmp-prompt-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var client = new ImageAwareClient();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noTools: true), conversation);

            await foreach (var _ in run.RunUserMessageEventsAsync(new ChatMessage(ChatRole.User,
                [new TextContent("inspect"), new DataContent(CreateBmp(), "image/bmp")]), "inspect")) { }

            Assert.Equal("image/png", Assert.Single(client.RequestImages).MediaType);
            Assert.Contains(client.RequestContents.Single().OfType<TextContent>(), text =>
                text.Text.Contains("[Image converted from image/bmp to image/png.]", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PositionalBmpConversionHintRemainsInItsFileReferenceThroughDispatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-positional-bmp-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "diagram.bmp"), CreateBmp());
            var promptFiles = await CliFileArguments.ProcessFilesAsync("describe", ["diagram.bmp"], root);
            var client = new ImageAwareClient();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noTools: true), conversation);
            var contents = new List<AIContent> { new TextContent(promptFiles.Text) };
            contents.AddRange(promptFiles.Images);

            await foreach (var _ in run.RunUserMessageEventsAsync(new ChatMessage(ChatRole.User, contents), promptFiles.Text)) { }

            Assert.Contains(client.RequestContents.Single().OfType<TextContent>(), text =>
                text.Text.Contains("<file name=\"" + Path.Combine(root, "diagram.bmp") + "\">" +
                    "[Image converted from image/bmp to image/png.]</file>", StringComparison.Ordinal));
            Assert.Equal("image/png", Assert.Single(client.RequestImages).MediaType);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DisablingPromptAutoResizePreservesSupportedImagesAndStillConvertsBmp()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-no-auto-resize-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourcePng = CreatePng(80, 40);
            var client = new ImageAwareClient();
            var conversation = new ConversationSession(root, "fixture", null);
            var agent = new PiAgent(client, new CodingTools(root,
                imageResizeOptions: new ModelImageResizeOptions(MaxWidth: 12, MaxHeight: 20, MaxBytes: 100, JpegQuality: 20),
                autoResizeImages: false), noTools: true);
            var run = await ConversationRun.OpenAsync(agent, conversation);

            await foreach (var _ in run.RunUserMessageEventsAsync(new ChatMessage(ChatRole.User,
                [new TextContent("original"), new DataContent(sourcePng, "image/png")]), "original")) { }
            await foreach (var _ in run.RunUserMessageEventsAsync(new ChatMessage(ChatRole.User,
                [new TextContent("convert"), new DataContent(CreateBmp(), "image/bmp")]), "convert")) { }

            Assert.Equal(sourcePng, client.RequestImages[0].Bytes);
            Assert.Equal(3, client.RequestImages.Count);
            Assert.Equal(sourcePng, client.RequestImages[1].Bytes);
            using var converted = SKBitmap.Decode(client.RequestImages[2].Bytes);
            Assert.Equal((1, 1), (converted!.Width, converted.Height));
            Assert.Equal("image/png", client.RequestImages[2].MediaType);
            Assert.Contains(client.RequestContents[1].OfType<TextContent>(), text =>
                text.Text.Contains("[Image converted from image/bmp to image/png.]", StringComparison.Ordinal));
            Assert.DoesNotContain(client.RequestContents[1].OfType<TextContent>(), text =>
                text.Text.Contains("displayed at", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConversationRunAcceptsAndPersistsImageContentInUserMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-multimodal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var imageBytes = CreatePng(2, 2);
            var client = new ImageAwareClient();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noTools: true), conversation);
            var message = new ChatMessage(ChatRole.User,
            [
                new TextContent("What is in this picture?"),
                new DataContent(imageBytes, "image/png")
            ]);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync(message, "What is in this picture?")) events.Add(item);

            Assert.True(client.SawImage);
            var history = conversation.ActiveMessages();
            Assert.Equal("image received", history.Last(message => message.Role == ChatRole.Assistant).Text);
            var restoredImage = Assert.IsType<DataContent>(history.Single(message => message.Role == ChatRole.User).Contents[1]);
            Assert.Equal("image/png", restoredImage.MediaType);
            Assert.Equal(imageBytes, restoredImage.Data.ToArray());
            Assert.Contains(events, item => item.Type == "agent_run_completed");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BlockImagesFiltersEveryProviderRequestWithoutErasingSessionHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-block-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = new DataContent(CreatePng(2, 2), "image/png");
            var client = new ImageAwareClient();
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noTools: true,
                blockImages: true), conversation);
            var message = new ChatMessage(ChatRole.User,
                [new TextContent("look"), image, image, new TextContent("again"), image]);
            await foreach (var _ in run.RunEventsAsync(message, "look")) { }
            await foreach (var _ in run.RunEventsAsync("another turn")) { }
            Assert.False(client.SawImage);
            Assert.Equal(2, client.RequestContents.Count);
            foreach (var request in client.RequestContents)
            {
                Assert.Empty(request.OfType<DataContent>());
                Assert.Equal(2, request.OfType<TextContent>().Count(content => content.Text == "Image reading is disabled."));
            }
            Assert.Equal(3, conversation.ActiveMessages().Where(item => item.Role == ChatRole.User)
                .SelectMany(item => item.Contents).OfType<DataContent>().Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BlockingImagesOnResumeFiltersHistoricalImagesWithoutChangingStoredTurns()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resumed-images-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var conversation = new ConversationSession(root, "fixture", null);
            var firstClient = new ImageAwareClient();
            var first = await ConversationRun.OpenAsync(new PiAgent(firstClient, new CodingTools(root), noTools: true), conversation);
            await foreach (var _ in first.RunEventsAsync(new ChatMessage(ChatRole.User,
                [new TextContent("look"), new DataContent(CreatePng(2, 2), "image/png")]), "look")) { }
            Assert.True(firstClient.SawImage);
            var secondClient = new ImageAwareClient();
            var resumed = await ConversationRun.OpenAsync(new PiAgent(secondClient, new CodingTools(root), noTools: true,
                blockImages: true), conversation);
            await foreach (var _ in resumed.RunEventsAsync("continue")) { }
            Assert.False(secondClient.SawImage);
            Assert.Contains(secondClient.RequestContents.Single().OfType<TextContent>(), item => item.Text == "Image reading is disabled.");
            Assert.Single(conversation.ActiveMessages().SelectMany(item => item.Contents).OfType<DataContent>());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ImageAwareClient : IChatClient
    {
        public bool SawImage { get; private set; }
        public List<IReadOnlyList<AIContent>> RequestContents { get; } = [];
        public List<(byte[] Bytes, string MediaType)> RequestImages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestContents.Add(messages.SelectMany(message => message.Contents).ToArray());
            var images = messages.SelectMany(message => message.Contents).OfType<DataContent>().ToArray();
            RequestImages.AddRange(images.Select(content => (content.Data.ToArray(), content.MediaType)));
            SawImage = images.Any(content => content.MediaType == "image/png");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "image received");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static byte[] CreateBmp()
    {
        var bytes = new byte[58];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)bytes.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), 54);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
        return bytes;
    }
}
