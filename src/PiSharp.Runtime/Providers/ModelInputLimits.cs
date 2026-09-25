using System.Text.Json;

namespace PiSharp.Runtime.Providers;

/// <summary>Image preprocessing options carried by Pi-compatible model metadata.</summary>
public sealed record ModelImageResizeOptions(int? MaxWidth = null, int? MaxHeight = null,
    int? MaxBytes = null, int? JpegQuality = null);

public sealed record ModelImageInputLimits(ModelImageResizeOptions? Resize = null);

/// <summary>The supported subset of a model's provider input limits.</summary>
public sealed record ModelInputLimits(ModelImageInputLimits? Images = null)
{
    /// <summary>Merge explicit model configuration over discovered metadata, field by field.</summary>
    public static ModelInputLimits? Merge(ModelInputLimits? preferred, ModelInputLimits? fallback)
    {
        var preferredResize = preferred?.Images?.Resize;
        var fallbackResize = fallback?.Images?.Resize;
        if (preferredResize is null && fallbackResize is null) return null;
        return new ModelInputLimits(new ModelImageInputLimits(new ModelImageResizeOptions(
            preferredResize?.MaxWidth ?? fallbackResize?.MaxWidth,
            preferredResize?.MaxHeight ?? fallbackResize?.MaxHeight,
            preferredResize?.MaxBytes ?? fallbackResize?.MaxBytes,
            preferredResize?.JpegQuality ?? fallbackResize?.JpegQuality)));
    }
}

/// <summary>Reads the supported resize profile from an upstream-shaped model object.</summary>
public static class ModelInputLimitsParser
{
    public static ModelInputLimits? Parse(JsonElement model, string source, bool strict)
    {
        if (!model.TryGetProperty("inputLimits", out var inputLimits) || inputLimits.ValueKind == JsonValueKind.Null)
            return null;
        if (inputLimits.ValueKind != JsonValueKind.Object)
            return Invalid(source, "inputLimits must be an object.", strict);
        if (!inputLimits.TryGetProperty("images", out var images) || images.ValueKind == JsonValueKind.Null)
            return null;
        if (images.ValueKind != JsonValueKind.Object)
            return Invalid(source, "inputLimits.images must be an object.", strict);
        if (!images.TryGetProperty("resize", out var resize) || resize.ValueKind == JsonValueKind.Null)
            return null;
        if (resize.ValueKind != JsonValueKind.Object)
            return Invalid(source, "inputLimits.images.resize must be an object.", strict);

        if (!TryReadInteger(resize, "maxWidth", 1, int.MaxValue, out var maxWidth) ||
            !TryReadInteger(resize, "maxHeight", 1, int.MaxValue, out var maxHeight) ||
            !TryReadInteger(resize, "maxBytes", 1, int.MaxValue, out var maxBytes) ||
            !TryReadInteger(resize, "jpegQuality", 1, 100, out var jpegQuality))
            return Invalid(source, "inputLimits.images.resize dimensions and maxBytes must be positive integers, and jpegQuality must be from 1 to 100.", strict);

        return new ModelInputLimits(new ModelImageInputLimits(
            new ModelImageResizeOptions(maxWidth, maxHeight, maxBytes, jpegQuality)));
    }

    private static bool TryReadInteger(JsonElement parent, string name, int minimum, int maximum, out int? result)
    {
        result = null;
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) ||
            number < minimum || number > maximum) return false;
        result = number;
        return true;
    }

    private static ModelInputLimits? Invalid(string source, string message, bool strict)
    {
        if (strict) throw new InvalidDataException($"{source} {message}");
        return null;
    }
}
