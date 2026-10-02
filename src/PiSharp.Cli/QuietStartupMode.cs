using System.Text.Json;

namespace PiSharp.Cli;

public enum QuietStartupMode { Full, Header, Silent }

internal static class QuietStartupModes
{
    public static QuietStartupMode Parse(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => QuietStartupMode.Silent,
        JsonValueKind.String when value.GetString() == "header" => QuietStartupMode.Header,
        _ => QuietStartupMode.Full
    };

    public static string ToSettingValue(this QuietStartupMode value) => value switch
    {
        QuietStartupMode.Silent => "true",
        QuietStartupMode.Header => "header",
        _ => "false"
    };
}
