using SkiaSharp;

namespace PiSharp.Runtime.Tools;

/// <summary>Photon's two-pass, unpremultiplied Lanczos3 sampling inside the existing Skia pipeline.</summary>
internal static class ReadImageResampler
{
    private const int MaximumRowPixels = 262_144;
    private readonly record struct Window(int Left, int Right, float Center, float Scale, float Sum, float[]? Weights)
    {
        public float At(int index) => Weights is not null ? Weights[index - Left] : Kernel((index - Center) / Scale) / Sum;
    }

    public static SKBitmap Resize(SKBitmap input, int width, int height, CancellationToken cancellationToken)
    {
        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        try
        {
            var source = input.GetPixelSpan();
            var target = result.GetPixelSpan();
            Window[]? columns = null;
            if (width <= 16_384 && (long)input.Width * 6 + width * 2 <= 1_048_576)
            {
                columns = new Window[width];
                for (var x = 0; x < width; x++) columns[x] = CreateWindow(input.Width, width, x, true, cancellationToken);
            }
            // Very wide inputs use direct vertical evaluation instead of a file-sized float row.
            var row = input.Width <= MaximumRowPixels ? new float[input.Width * 4] : null;
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vertical = CreateWindow(input.Height, height, y, true, cancellationToken);
                if (row is not null)
                    for (var x = 0; x < input.Width; x++)
                    {
                        var value = SampleVertical(source, input.RowBytes, x, vertical, cancellationToken);
                        var index = x * 4;
                        row[index] = value.R; row[index + 1] = value.G;
                        row[index + 2] = value.B; row[index + 3] = value.A;
                    }
                for (var x = 0; x < width; x++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var horizontal = columns is not null ? columns[x] : CreateWindow(input.Width, width, x, false, cancellationToken);
                    float r = 0, g = 0, b = 0, a = 0;
                    for (var index = horizontal.Left; index < horizontal.Right; index++)
                    {
                        if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var value = row is not null ? (row[index * 4], row[index * 4 + 1], row[index * 4 + 2], row[index * 4 + 3]) :
                            SampleVertical(source, input.RowBytes, index, vertical, cancellationToken);
                        var weight = horizontal.At(index);
                        r += value.Item1 * weight; g += value.Item2 * weight;
                        b += value.Item3 * weight; a += value.Item4 * weight;
                    }
                    var pixel = y * result.RowBytes + x * 4;
                    target[pixel] = Channel(r); target[pixel + 1] = Channel(g);
                    target[pixel + 2] = Channel(b); target[pixel + 3] = Channel(a);
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static (float R, float G, float B, float A) SampleVertical(ReadOnlySpan<byte> source, int stride, int x,
        Window window, CancellationToken cancellationToken)
    {
        float r = 0, g = 0, b = 0, a = 0;
        for (var y = window.Left; y < window.Right; y++)
        {
            if ((y & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var pixel = y * stride + x * 4;
            var weight = window.At(y);
            r += source[pixel] * weight; g += source[pixel + 1] * weight;
            b += source[pixel + 2] * weight; a += source[pixel + 3] * weight;
        }
        return (r, g, b, a);
    }

    private static Window CreateWindow(int source, int destination, int pixel, bool cache, CancellationToken cancellationToken)
    {
        var ratio = (float)source / destination;
        var scale = MathF.Max(1, ratio);
        var center = (pixel + 0.5f) * ratio;
        var left = Math.Clamp((int)MathF.Floor(center - 3 * scale), 0, source - 1);
        var right = Math.Clamp((int)MathF.Ceiling(center + 3 * scale), left + 1, source);
        center -= 0.5f;
        var weights = cache && right - left <= 4096 ? new float[right - left] : null;
        float sum = 0;
        for (var index = left; index < right; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = Kernel((index - center) / scale);
            if (weights is not null) weights[index - left] = value;
            sum += value;
        }
        if (weights is not null)
            for (var index = 0; index < weights.Length; index++) weights[index] /= sum;
        return new(left, right, center, scale, sum, weights);
    }

    private static float Kernel(float value) => MathF.Abs(value) < 3 ? Sinc(value) * Sinc(value / 3) : 0;
    private static float Sinc(float value) => value == 0 ? 1 : MathF.Sin(value * MathF.PI) / (value * MathF.PI);
    private static byte Channel(float value) => (byte)MathF.Round(Math.Clamp(value, 0, 255), MidpointRounding.AwayFromZero);
}
