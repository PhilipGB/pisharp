using PiSharp.Runtime.Tools;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class ReadImageCancellationTests
{
    [Fact]
    public async Task CancellationSettlesWhileDecodedImageWorkIsStillUnwinding()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-image-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var decoded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ReadToolOutput>? read = null;
        try
        {
            using var bitmap = new SKBitmap(1200, 800);
            bitmap.Erase(SKColors.CornflowerBlue);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(Path.Combine(root, "image.png"), encoded.ToArray());
            var reader = new FileReader(root, null, () =>
            {
                decoded.TrySetResult();
                release.Wait();
                released.TrySetResult();
            });
            read = reader.ReadAsync("image.png", 1, null, cancellation.Token);
            await decoded.Task.WaitAsync(TimeSpan.FromSeconds(5));

            cancellation.Cancel();

            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await read.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("Operation aborted", error.Message);
            Assert.True(read.IsCanceled);
            Assert.False(released.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            if (read is not null)
            {
                try { await read.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
                await released.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
