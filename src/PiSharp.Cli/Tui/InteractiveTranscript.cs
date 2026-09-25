using System.Text;
using System.Text.Json;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

/// <summary>Renders agent lifecycle events as normal-screen transcript output.</summary>
public sealed class InteractiveTranscript(TextWriter output, TextWriter status, bool interactive = true,
    bool hideThinking = false, TerminalScreen? screen = null)
{
    private readonly Dictionary<string, ShellOutputNormalizer> _liveBash = new(StringComparer.Ordinal);
    private readonly StringBuilder _assistantMarkdown = new();

    public bool HasAssistantOutput { get; private set; }
    public int ExitCode { get; private set; }

    public void Render(AgentLifecycleEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Type != "model_text_delta") CommitAssistantText();
        switch (update.Type)
        {
            case "prompt_accepted" when interactive && screen is not null:
                screen.AppendUserMessage(Safe(update.Text ?? ""), update.Images);
                break;
            case "model_text_delta" when !string.IsNullOrEmpty(update.Text):
                var assistantText = Safe(update.Text);
                if (screen is null) output.Write(assistantText);
                else
                {
                    _assistantMarkdown.Append(assistantText);
                    screen.SetAssistantText(_assistantMarkdown.ToString());
                }
                HasAssistantOutput = true;
                break;
            case "reasoning_delta" when interactive && !hideThinking && !string.IsNullOrEmpty(update.Text):
                status.Write(Safe(update.Text));
                break;
            case "usage" when interactive && !string.IsNullOrEmpty(update.Text):
                status.WriteLine($"\nUsage: {Safe(update.Text)}");
                break;
            case "context_compacted" when interactive && !string.IsNullOrEmpty(update.Text):
                status.WriteLine(Safe(update.Text));
                break;
            case "tool_execution_started" when interactive:
                var summary = ToolCallSummary.Format(update.Tool, update.ToolArguments);
                status.WriteLine($"\n→ {Safe(update.Tool ?? "tool")}{(summary is null ? "" : $" · {Safe(summary)}")}" +
                    (update.OperationId is null ? "" : $" ({Safe(update.OperationId)})"));
                break;
            case "tool_execution_update" when interactive && update.Tool == "bash" && !string.IsNullOrEmpty(update.Text):
                WriteBashUpdate(update);
                break;
            case "tool_execution_finished" when interactive:
                FinishTool(update);
                break;
            case "turn_failed" or "prompt_rejected":
                ExitCode = Math.Max(ExitCode, 1);
                status.WriteLine($"Agent error: {Safe(update.Error ?? update.Type)}");
                break;
            case "turn_interrupted":
                status.WriteLine("Interrupted.");
                break;
        }
    }

    public void FinishTurn()
    {
        CommitAssistantText();
        if (!HasAssistantOutput) return;
        output.WriteLine();
        HasAssistantOutput = false;
    }

    private void CommitAssistantText()
    {
        if (screen is null || _assistantMarkdown.Length == 0) return;
        screen.CommitAssistantText(_assistantMarkdown.ToString());
        _assistantMarkdown.Clear();
    }

    private void WriteBashUpdate(AgentLifecycleEvent update)
    {
        var text = update.Text!;
        if (update.OperationId is { } id)
        {
            if (!_liveBash.TryGetValue(id, out var normalizer))
                _liveBash.Add(id, normalizer = new ShellOutputNormalizer());
            text = normalizer.Append(text.AsSpan());
        }
        else text = ShellOutputNormalizer.NormalizeComplete(text);
        if (text.Length != 0) status.Write(Safe(text));
    }

    private void FinishTool(AgentLifecycleEvent update)
    {
        if (update.Tool == "bash" && update.OperationId is { } id && _liveBash.Remove(id, out var normalizer))
        {
            var trailing = normalizer.Finish();
            if (trailing.Length != 0) status.Write(Safe(trailing));
            status.WriteLine();
            var error = update.Error;
            var statusStart = error?.LastIndexOf("\n\nCommand ", StringComparison.Ordinal) ?? -1;
            if (statusStart >= 0) error = error![(statusStart + 2)..];
            if (error is not null) error = ShellOutputNormalizer.NormalizeComplete(error);
            status.WriteLine($"← {(update.IsError == true ? Safe(error ?? "bash failed") : "bash completed")}");
        }
        else
        {
            var result = update.IsError == true ? update.Error : update.Text;
            if (update.Tool == "bash" && result is not null)
                result = ShellOutputNormalizer.NormalizeComplete(result);
            var rendered = $"← {(result is null ? update.IsError == true ? "tool failed" : "completed" : Safe(result))}{Environment.NewLine}";
            if (screen is null) status.Write(rendered);
            else screen.AppendToolResult(rendered, update.Images);
        }

        if (update.Details is not null && ToolDetailsSummary.TryFormat(update.Details, out var summary))
            status.WriteLine($"  {Safe(summary)}");
    }

    private string Safe(string value) => interactive ? TerminalSafeText.Normalize(value) : value;
}

internal static class TerminalSafeText
{
    public static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value == '\n') result.Append('\n');
            else if (rune.Value == '\t') result.Append("    ");
            else if (!Rune.IsControl(rune)) result.Append(rune.ToString());
        }
        return result.ToString();
    }
}

internal static class ToolDetailsSummary
{
    public static bool TryFormat(object details, out string summary)
    {
        summary = "";
        try
        {
            var root = details is JsonElement element ? element : JsonSerializer.SerializeToElement(details);
            if (root.ValueKind != JsonValueKind.Object) return false;
            var parts = new List<string>();
            AddLimit(root, "matchLimitReached", "match limit reached", parts);
            AddLimit(root, "resultLimitReached", "result limit reached", parts);
            AddLimit(root, "entryLimitReached", "entry limit reached", parts);
            if (TryProperty(root, "linesTruncated", out var linesTruncated) && linesTruncated.ValueKind == JsonValueKind.True)
                parts.Add("one or more matching lines were truncated");
            if (TryProperty(root, "truncation", out var truncation) && truncation.ValueKind == JsonValueKind.Object &&
                TryProperty(truncation, "truncated", out var wasTruncated) && wasTruncated.ValueKind == JsonValueKind.True)
            {
                var by = GetString(truncation, "truncatedBy");
                var totalBytes = GetInteger(truncation, "totalBytes");
                var outputBytes = GetInteger(truncation, "outputBytes");
                var totalLines = GetInteger(truncation, "totalLines");
                var outputLines = GetInteger(truncation, "outputLines");
                var reason = by is null ? "output truncated" : $"output truncated by {by}";
                if (totalBytes is not null && outputBytes is not null)
                    reason += $" ({outputBytes} of {totalBytes} bytes shown)";
                else if (totalLines is not null && outputLines is not null)
                    reason += $" ({outputLines} of {totalLines} lines shown)";
                parts.Add(reason);
            }
            if (parts.Count == 0) return false;
            summary = string.Join("; ", parts);
            return true;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void AddLimit(JsonElement root, string property, string label, ICollection<string> parts)
    {
        if (!TryProperty(root, property, out var value)) return;
        var limit = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : (long?)null;
        parts.Add(limit is null ? label : $"{label} ({limit})");
    }

    private static long? GetInteger(JsonElement root, string name) => TryProperty(root, name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static string? GetString(JsonElement root, string name) => TryProperty(root, name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }
}
