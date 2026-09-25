using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

/// <summary>Builds bounded themed tool views while keeping extension callbacks outside screen state.</summary>
internal sealed class TerminalToolPresentation(Func<string, PiSharpToolRenderer?>? resolveRenderer,
    string workingDirectory, Func<bool>? isExpanded = null)
{
    private const int MaximumSpans = 1_024;
    private const int MaximumCharacters = 32_768;
    private const int MaximumLines = 256;
    private const int MaximumArgumentBytes = 256 * 1024;
    private const int MaximumRememberedCalls = 1_024;
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> _argumentsByCall = new(StringComparer.Ordinal);
    private readonly Queue<string> _callOrder = new();

    public PiSharpToolRenderView RenderCall(AgentLifecycleEvent update)
    {
        var tool = update.Tool ?? "tool";
        var arguments = SnapshotArguments(update.ToolArguments);
        var renderer = Resolve(tool);
        if (renderer?.RenderResult is not null && update.OperationId is { Length: > 0 } operationId)
            RememberArguments(operationId, arguments);
        if (renderer?.RenderCall is { } custom)
        {
            try
            {
                var returned = custom(arguments,
                    Context(update, arguments, executionStarted: true, argumentsComplete: true, isPartial: false, isError: false));
                if (returned is not null)
                {
                    var view = SnapshotView(returned);
                    if (HasText(view)) return view;
                }
            }
            catch (Exception error) when (IsRendererError(error)) { }
        }

        var summary = ToolCallSummary.Format(update.Tool, arguments);
        var spans = new List<PiSharpToolTextSpan>
        {
            new("→ ", PiSharpToolTextStyle.Muted),
            new(tool, PiSharpToolTextStyle.Title, Bold: true)
        };
        if (summary is not null)
        {
            spans.Add(new(" · ", PiSharpToolTextStyle.Muted));
            spans.Add(new(summary, PiSharpToolTextStyle.Output));
        }
        if (update.OperationId is { } id)
        {
            spans.Add(new(" (", PiSharpToolTextStyle.Muted));
            spans.Add(new(id, PiSharpToolTextStyle.Muted));
            spans.Add(new(")", PiSharpToolTextStyle.Muted));
        }
        return new(spans);
    }

    public PiSharpToolRenderView RenderResult(AgentLifecycleEvent update, bool isError)
    {
        var tool = update.Tool ?? "tool";
        var renderer = Resolve(tool);
        var arguments = TakeArguments(update);
        if (renderer?.RenderResult is { } custom)
        {
            try
            {
                var result = new PiSharpToolRenderResult(update.Text, update.Details, update.Images, isError, update.Error);
                var returned = custom(result, Context(update, arguments, executionStarted: true,
                    argumentsComplete: true, isPartial: false, isError: isError));
                if (returned is not null)
                {
                    var view = SnapshotView(returned);
                    if (HasText(view)) return view;
                }
            }
            catch (Exception error) when (IsRendererError(error)) { }
        }

        var text = isError
            ? update.Error ?? update.Text ?? "tool failed"
            : update.Tool == "bash" && update.Text is not null
                ? ShellOutputNormalizer.NormalizeComplete(update.Text)
                : update.Text ?? "completed";
        var spans = new List<PiSharpToolTextSpan>
        {
            new("← ", isError ? PiSharpToolTextStyle.Error : PiSharpToolTextStyle.Muted),
            new(TerminalSafeText.Normalize(text), isError ? PiSharpToolTextStyle.Error : PiSharpToolTextStyle.Output)
        };
        if (update.Details is not null && ToolDetailsSummary.TryFormat(update.Details, out var summary))
        {
            spans.Add(new("\n  ", PiSharpToolTextStyle.Muted));
            spans.Add(new(TerminalSafeText.Normalize(summary), PiSharpToolTextStyle.Muted));
        }
        return new(spans);
    }

    public static string Render(PiSharpToolRenderView view, TerminalTheme? theme)
    {
        ArgumentNullException.ThrowIfNull(view);
        var output = new StringBuilder();
        var usedCharacters = 0;
        var usedLines = 1;
        var truncated = false;
        var visitedSpans = 0;
        foreach (var span in view.Spans ?? [])
        {
            if (++visitedSpans > MaximumSpans) { truncated = true; break; }
            if (span is null) continue;
            if (output.Length > 0 && usedCharacters >= MaximumCharacters) { truncated = true; break; }
            var safe = SafeBoundedText(span.Text, ref usedCharacters, ref usedLines, out var spanTruncated);
            truncated |= spanTruncated;
            if (safe.Length > 0)
            {
                var token = Token(span.Style);
                output.Append(theme is null || token is null
                    ? safe
                    : theme.Style(token, safe, bold: span.Bold || span.Style == PiSharpToolTextStyle.Title,
                        dim: span.Dim || span.Style == PiSharpToolTextStyle.Muted));
            }
            if (truncated || usedCharacters >= MaximumCharacters || usedLines > MaximumLines) { truncated = true; break; }
        }
        if (truncated)
        {
            if (output.Length > 0 && !output.ToString().EndsWith('\n')) output.Append('\n');
            const string notice = "… (extension tool view truncated)";
            output.Append(theme is null ? notice : theme.Style("muted", notice, dim: true));
        }
        return output.ToString();
    }

    private PiSharpToolRenderContext Context(AgentLifecycleEvent update,
        IReadOnlyDictionary<string, object?> arguments, bool executionStarted, bool argumentsComplete,
        bool isPartial, bool isError) => new(update.Tool ?? "tool", update.OperationId,
        workingDirectory, arguments, executionStarted, argumentsComplete, isPartial, isExpanded?.Invoke() ?? false, isError);

    private void RememberArguments(string operationId, IReadOnlyDictionary<string, object?> arguments)
    {
        if (_argumentsByCall.ContainsKey(operationId)) return;
        _argumentsByCall.Add(operationId, arguments);
        _callOrder.Enqueue(operationId);
        while (_callOrder.Count > MaximumRememberedCalls)
            _argumentsByCall.Remove(_callOrder.Dequeue());
    }

    private IReadOnlyDictionary<string, object?> TakeArguments(AgentLifecycleEvent update)
    {
        if (update.OperationId is { Length: > 0 } operationId && _argumentsByCall.Remove(operationId, out var arguments))
            return arguments;
        return SnapshotArguments(update.ToolArguments);
    }

    private static IReadOnlyDictionary<string, object?> SnapshotArguments(IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0) return EmptyArguments;
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(arguments);
            if (json.Length > MaximumArgumentBytes) return EmptyArguments;
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind != JsonValueKind.Object
                ? EmptyArguments
                : new ReadOnlyDictionary<string, object?>(document.RootElement.EnumerateObject()
                    .ToDictionary(property => property.Name, property => CopyValue(property.Value), StringComparer.Ordinal));
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return EmptyArguments;
        }
    }

    private PiSharpToolRenderer? Resolve(string name)
    {
        try { return resolveRenderer?.Invoke(name); }
        catch (Exception error) when (IsRendererError(error)) { return null; }
    }

    private static string? Token(PiSharpToolTextStyle style) => style switch
    {
        PiSharpToolTextStyle.Title => "toolTitle",
        PiSharpToolTextStyle.Output => "toolOutput",
        PiSharpToolTextStyle.Muted => "muted",
        PiSharpToolTextStyle.Accent => "accent",
        PiSharpToolTextStyle.Code => "mdCode",
        PiSharpToolTextStyle.Success => "success",
        PiSharpToolTextStyle.Error => "error",
        _ => null
    };

    private static bool HasText(PiSharpToolRenderView? view) =>
        view?.Spans?.Any(span => span is not null && !string.IsNullOrWhiteSpace(span.Text)) == true;

    private static PiSharpToolRenderView SnapshotView(PiSharpToolRenderView view)
    {
        var spans = new List<PiSharpToolTextSpan>();
        var usedCharacters = 0;
        var usedLines = 1;
        var truncated = false;
        foreach (var span in view.Spans ?? [])
        {
            if (spans.Count >= MaximumSpans) { truncated = true; break; }
            if (span is null) continue;
            var text = SafeBoundedText(span.Text, ref usedCharacters, ref usedLines, out var spanTruncated);
            if (text.Length > 0)
                spans.Add(new(text, Enum.IsDefined(span.Style) ? span.Style : PiSharpToolTextStyle.Normal, span.Bold, span.Dim));
            if (spanTruncated || usedCharacters >= MaximumCharacters || usedLines >= MaximumLines)
            {
                truncated = true;
                break;
            }
        }
        if (truncated && spans.Count < MaximumSpans)
            spans.Add(new("… (extension tool view truncated)", PiSharpToolTextStyle.Muted));
        return new(spans.ToArray());
    }

    private static object? CopyValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => value.EnumerateArray().Select(CopyValue).ToArray(),
        JsonValueKind.Object => new ReadOnlyDictionary<string, object?>(value.EnumerateObject()
            .ToDictionary(property => property.Name, property => CopyValue(property.Value), StringComparer.Ordinal)),
        _ => value.Clone()
    };

    private static string SafeBoundedText(string? value, ref int usedCharacters, ref int usedLines, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(value)) return "";
        var output = new StringBuilder(Math.Min(value.Length, MaximumCharacters - usedCharacters));
        var inspectedRunes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (++inspectedRunes > MaximumCharacters * 2) { truncated = true; break; }
            if (rune.Value == '\n')
            {
                if (usedLines >= MaximumLines) { truncated = true; break; }
                output.Append('\n');
                usedCharacters++;
                usedLines++;
            }
            else if (rune.Value == '\t')
            {
                if (usedCharacters + 4 > MaximumCharacters) { truncated = true; break; }
                output.Append("    ");
                usedCharacters += 4;
            }
            else if (Rune.IsControl(rune)) continue;
            else
            {
                var runeText = rune.ToString();
                if (usedCharacters + runeText.Length > MaximumCharacters) { truncated = true; break; }
                output.Append(runeText);
                usedCharacters += runeText.Length;
            }
        }
        if (!truncated && output.Length != value.Length) truncated = usedCharacters >= MaximumCharacters;
        return output.ToString();
    }

    private static bool IsRendererError(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static readonly IReadOnlyDictionary<string, object?> EmptyArguments =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));
}
