namespace PiSharp.Cli.Tui;

/// <summary>Adapts the reusable list overlay to user messages that can start a fork.</summary>
internal sealed class TerminalForkPicker(TerminalEditor editor)
{
    private const int MaximumSearchCharacters = 4096;
    private const int MaximumPreviewSourceCharacters = 512;
    private const int MaximumPreviewCells = 120;

    public (string Id, string Text)? Show(IReadOnlyList<(string Id, string Text)> messages)
    {
        if (messages.Count == 0) return null;
        var options = messages.Select((message, index) =>
        {
            var id = message.Id[..Math.Min(12, message.Id.Length)];
            var preview = Preview(message.Text);
            return new TerminalSelectionOption<(string Id, string Text)>(message.Id, message,
                $"{id} · {preview}", $"Message {index + 1} of {messages.Count}",
                $"{message.Id} {SearchableExcerpt(message.Text)}");
        }).ToArray();
        var chosen = editor.ShowSelectionList("Fork from message", options, messages[^1].Id,
            emptyMessage: "No text-only user messages on this branch");
        return chosen?.Option.Value;
    }

    private static string SearchableExcerpt(string text)
    {
        var length = Math.Min(text.Length, MaximumSearchCharacters);
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    private static string Preview(string text)
    {
        var lineEnd = text.IndexOfAny(['\r', '\n']);
        if (lineEnd < 0) lineEnd = text.Length;
        var length = Math.Min(lineEnd, MaximumPreviewSourceCharacters);
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        var preview = TerminalSafeText.Normalize(text[..length]).Replace('\t', ' ');
        var clipped = TerminalTranscriptViewport.Clip(preview, MaximumPreviewCells);
        return length < lineEnd ? $"{clipped}…" : clipped;
    }
}
