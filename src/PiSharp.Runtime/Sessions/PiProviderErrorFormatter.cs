using System.Text.Json;

namespace PiSharp.Runtime.Sessions;

internal static class PiProviderErrorFormatter
{
    public static string? Format(string? error)
    {
        if (error is null || !error.StartsWith("HTTP ", StringComparison.Ordinal)) return error;
        var statusEnd = error.IndexOf(' ', 5);
        var bodyStart = error.IndexOf("\n\n", StringComparison.Ordinal);
        if (statusEnd < 0 || bodyStart < 0 ||
            !int.TryParse(error.AsSpan(5, statusEnd - 5), out var status)) return error;
        var message = error[(bodyStart + 2)..].Trim();
        return message.Length == 0 ? error : $"{status}: {JsonSerializer.Serialize(new { message })}";
    }
}
