using System.Globalization;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.VirtualModels;

namespace PiSharp.Cli.Tui;

internal static class TerminalModelStatus
{
    public static string Format(string provider, ModelDescriptor model, string thinking,
        ConversationSession session, int? contextWindow)
    {
        var selected = $"{provider}/{model.Id} · thinking {thinking}";
        if (model.Api != VirtualModelContract.Api) return selected;
        var response = session.ActiveMessages().LastOrDefault(message => message.Role == ChatRole.Assistant &&
            message.AdditionalProperties is { } properties &&
            ChatMessageProperties.String(properties, "pisharp.stopReason") is not ("error" or "aborted") &&
            ChatMessageProperties.String(properties, "pisharp.model") is not null);
        if (response?.AdditionalProperties is not { } physical) return selected;
        var routed = ChatMessageProperties.String(physical, "pisharp.model");
        var level = ChatMessageProperties.String(physical, "pisharp.thinkingLevel");
        return selected + $" → {routed}" + (level is null ? "" : $" · thinking {level}") +
            (contextWindow is > 0 ? $" · context {contextWindow.Value.ToString("N0", CultureInfo.InvariantCulture)}" : "");
    }
}
