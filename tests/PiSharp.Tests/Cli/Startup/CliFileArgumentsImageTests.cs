using Microsoft.Extensions.AI;
using PiSharp.Cli;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class CliFileArgumentsImageTests
{
    [Theory]
    [InlineData("diagram.jpg")]
    [InlineData("diagram.data")]
    public async Task PositionalImageMimeIsDetectedFromContent(string filename)
    {
        var root = CreateDirectory();
        try
        {
            var bytes = CreatePng(2, 1);
            await File.WriteAllBytesAsync(Path.Combine(root, filename), bytes);

            var prompt = await CliFileArguments.ProcessFilesAsync("describe", [filename], root);

            var image = Assert.Single(prompt.Images);
            Assert.Equal("image/png", image.MediaType);
            Assert.Equal(bytes, image.Data.ToArray());
            Assert.Equal($"<file name=\"{System.Security.SecurityElement.Escape(Path.Combine(root, filename))}\"></file>\ndescribe",
                prompt.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PositionalImageSignaturePrefixWithoutACompleteImageIsNotForwarded()
    {
        var root = CreateDirectory();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "incomplete.png"),
                [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                CliFileArguments.ProcessFilesAsync("describe", ["incomplete.png"], root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OversizedPositionalImageIsOmittedAndPromptContinues()
    {
        var root = CreateDirectory();
        try
        {
            var path = Path.Combine(root, "oversized.png");
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(new byte[]
                {
                    0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
                    0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52
                });
                stream.SetLength(20 * 1024 * 1024 + 1L);
            }

            var prompt = await CliFileArguments.ProcessFilesAsync("continue with the prompt", ["oversized.png"], root);

            Assert.Empty(prompt.Images);
            Assert.Contains("[Image omitted: exceeds the 20 MiB input limit.]", prompt.Text, StringComparison.Ordinal);
            Assert.EndsWith("continue with the prompt", prompt.Text, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BoundedImageReaderRejectsGrowthAfterItsLengthSnapshot()
    {
        await using var stream = new GrowingStream(3, [1, 2, 3, 4]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CliFileArguments.ReadBoundedImageAsync(stream, 3, "growing.png", CancellationToken.None));
    }

    [Fact]
    public async Task PositionalBmpIsConvertedBeforeDispatchAndItsHintStaysInsideFileReference()
    {
        var root = CreateDirectory();
        try
        {
            var path = Path.Combine(root, "diagram.bmp");
            await File.WriteAllBytesAsync(path, CreateBmp());

            var prompt = await CliFileArguments.ProcessFilesAsync("describe", ["diagram.bmp"], root);

            var image = Assert.Single(prompt.Images);
            Assert.Equal("image/png", image.MediaType);
            Assert.True(image.Data.Span.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47 }));
            Assert.Equal($"<file name=\"{System.Security.SecurityElement.Escape(path)}\">" +
                "[Image converted from image/bmp to image/png.]</file>\ndescribe", prompt.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PositionalFilesKeepTextImageOrderDuplicatesAndUnicodePaths()
    {
        var root = CreateDirectory();
        try
        {
            var textName = "notes λ.txt";
            var pngName = "diagram one.png";
            var bmpName = "photo two.bmp";
            await File.WriteAllTextAsync(Path.Combine(root, textName), "read me");
            var png = CreatePng(2, 1);
            await File.WriteAllBytesAsync(Path.Combine(root, pngName), png);
            await File.WriteAllBytesAsync(Path.Combine(root, bmpName), CreateBmp());

            var prompt = await CliFileArguments.ProcessFilesAsync("analyze", [textName, pngName, bmpName, pngName], root);

            Assert.Equal(["image/png", "image/png", "image/png"], prompt.Images.Select(image => image.MediaType));
            Assert.Equal(png, prompt.Images[0].Data.ToArray());
            Assert.Equal(png, prompt.Images[2].Data.ToArray());
            Assert.Contains($"<file name=\"{Path.Combine(root, textName)}\">\nread me\n</file>", prompt.Text);
            var textReferenceIndex = prompt.Text.IndexOf(Path.Combine(root, textName), StringComparison.Ordinal);
            var pngReferenceIndex = prompt.Text.IndexOf(Path.Combine(root, pngName), StringComparison.Ordinal);
            var bmpReferenceIndex = prompt.Text.IndexOf(Path.Combine(root, bmpName), StringComparison.Ordinal);
            Assert.True(textReferenceIndex >= 0 && pngReferenceIndex > textReferenceIndex && bmpReferenceIndex > pngReferenceIndex);
            Assert.Equal(2, prompt.Text.Split(Path.Combine(root, pngName), StringSplitOptions.None).Length - 1);
            Assert.Contains("[Image converted from image/bmp to image/png.]", prompt.Text, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-at-image-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateBmp()
    {
        var bytes = new byte[58];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes((uint)bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes((uint)54).CopyTo(bytes, 10);
        BitConverter.GetBytes((uint)40).CopyTo(bytes, 14);
        BitConverter.GetBytes(1).CopyTo(bytes, 18);
        BitConverter.GetBytes(1).CopyTo(bytes, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((ushort)24).CopyTo(bytes, 28);
        bytes[56] = 0xff;
        return bytes;
    }

    private sealed class GrowingStream(long initialLength, byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => initialLength;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, data.Length - _position);
            data.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset, read));
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
