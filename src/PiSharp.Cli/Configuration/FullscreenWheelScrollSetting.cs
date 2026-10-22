using System.Globalization;
using System.Text.Json;

namespace PiSharp.Cli;

/// <summary>Configured fullscreen wheel distance, or Pi's velocity-based automatic mode.</summary>
public readonly record struct FullscreenWheelScrollSetting(bool Auto, int Lines)
{
    public static FullscreenWheelScrollSetting Automatic => new(true, 1);

    public static FullscreenWheelScrollSetting Fixed(int lines)
    {
        if (lines is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(lines));
        return new(false, lines);
    }

    public static FullscreenWheelScrollSetting Parse(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when value.GetString() == "auto" => Automatic,
        JsonValueKind.Number when value.TryGetDouble(out var lines) && double.IsFinite(lines) =>
            Fixed((int)Math.Clamp(Math.Floor(lines), 1, 100)),
        _ => throw new InvalidDataException("settings.json fullscreenWheelScrollLines must be 'auto' or an integer from 1 to 100.")
    };

    public static FullscreenWheelScrollSetting ParseSettingValue(string value)
    {
        if (value == "auto") return Automatic;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var lines) && lines is >= 1 and <= 100)
            return Fixed(lines);
        throw new ArgumentException("Fullscreen wheel scrolling must be 'auto' or an integer from 1 to 100.", nameof(value));
    }

    public string ToSettingValue() => Auto ? "auto" : Lines.ToString(CultureInfo.InvariantCulture);
}
