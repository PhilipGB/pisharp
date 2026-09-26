namespace PiSharp.Runtime.Sessions;

public enum PromptDeliveryMode
{
    OneAtATime,
    All
}

public static class PromptDeliveryModes
{
    public static string ToSettingValue(this PromptDeliveryMode mode) => mode switch
    {
        PromptDeliveryMode.OneAtATime => "one-at-a-time",
        PromptDeliveryMode.All => "all",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static bool TryParseSettingValue(string? value, out PromptDeliveryMode mode)
    {
        switch (value)
        {
            case "one-at-a-time":
                mode = PromptDeliveryMode.OneAtATime;
                return true;
            case "all":
                mode = PromptDeliveryMode.All;
                return true;
            default:
                mode = PromptDeliveryMode.OneAtATime;
                return false;
        }
    }
}
