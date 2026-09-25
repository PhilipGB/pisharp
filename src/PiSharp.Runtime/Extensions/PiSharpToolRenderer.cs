using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Extensions;

/// <summary>Semantic styles rendered by PiSharp's active terminal theme.</summary>
public enum PiSharpToolTextStyle
{
    Normal,
    Title,
    Output,
    Muted,
    Accent,
    Code,
    Success,
    Error
}

/// <summary>A terminal-safe text fragment with a semantic theme style.</summary>
public sealed record PiSharpToolTextSpan(string Text, PiSharpToolTextStyle Style = PiSharpToolTextStyle.Normal,
    bool Bold = false, bool Dim = false);

/// <summary>Plain text and semantic styles returned by an extension renderer.</summary>
public sealed record PiSharpToolRenderView(IReadOnlyList<PiSharpToolTextSpan> Spans)
{
    public static PiSharpToolRenderView FromText(string text, PiSharpToolTextStyle style = PiSharpToolTextStyle.Normal) =>
        new([new(text, style)]);
}

/// <summary>Read-only context shared by an extension's call and result rendering.</summary>
public sealed record PiSharpToolRenderContext(
    string ToolName,
    string? ToolCallId,
    string WorkingDirectory,
    IReadOnlyDictionary<string, object?> Arguments,
    bool ExecutionStarted,
    bool ArgumentsComplete,
    bool IsPartial,
    bool IsExpanded,
    bool IsError);

/// <summary>Result data supplied to a tool result renderer.</summary>
public sealed record PiSharpToolRenderResult(string? Text, object? Details,
    IReadOnlyList<DataContent>? Images, bool IsError, string? Error = null);

public delegate PiSharpToolRenderView? PiSharpToolCallRenderer(
    IReadOnlyDictionary<string, object?> arguments, PiSharpToolRenderContext context);

public delegate PiSharpToolRenderView? PiSharpToolResultRenderer(
    PiSharpToolRenderResult result, PiSharpToolRenderContext context);

/// <summary>
/// Optional extension presentation callbacks. Return null to use PiSharp's standard safe view.
/// Views contain text and semantic styles only; the host owns ANSI output and layout.
/// </summary>
public sealed class PiSharpToolRenderer
{
    public PiSharpToolRenderer(PiSharpToolCallRenderer? renderCall = null,
        PiSharpToolResultRenderer? renderResult = null)
    {
        if (renderCall is null && renderResult is null)
            throw new ArgumentException("A tool renderer must provide a call or result callback.");
        RenderCall = renderCall;
        RenderResult = renderResult;
    }

    public PiSharpToolCallRenderer? RenderCall { get; }
    public PiSharpToolResultRenderer? RenderResult { get; }
}
