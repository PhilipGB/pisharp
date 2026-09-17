using System.Text.RegularExpressions;

namespace PiSharp.Core;

/// <summary>
/// Detects provider context-overflow responses, ported from Pi's overflow policy. A provider
/// overflow response is authoritative: compaction must be attempted regardless of the local
/// token estimate. Patterns are matched against error text; known non-overflow conditions
/// (rate limiting, throttling) are excluded first.
/// </summary>
public static class ContextOverflowPolicy
{
    private static readonly Regex[] OverflowPatterns =
    [
        new("prompt is too long", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("request_too_large", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("input is too long for requested model", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("exceeds the context window", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("exceeds (?:the )?(?:model'?s )?maximum context length(?: of [\\d,]+ tokens?|\\s*\\([\\d,]+\\))", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("input token count.*exceeds the maximum", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("maximum prompt length is \\d+", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("reduce the length of the messages", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("maximum context length is \\d+ tokens", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("exceeds (?:the )?maximum allowed input length of [\\d,]+ tokens?", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("input \\(\\d+ tokens\\) is longer than the model'?s context length \\(\\d+ tokens\\)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("exceeds the limit of \\d+", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("exceeds the available context size", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("greater than the context length", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("context window exceeds limit", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("exceeded model token limit", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("too large for model with \\d+ maximum context length", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("prompt has [\\d,]+ tokens?, but the configured context size is [\\d,]+ tokens?", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("model_context_window_exceeded", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("prompt too long; exceeded (?:max )?context length", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("range of input length should be", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("context[_ ]length[_ ]exceeded", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("too many tokens", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("token limit exceeded", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        // Cerebras surfaces 400/413 with no body.
        new("^4(?:00|13)\\s*(?:status code)?\\s*\\(no body\\)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
    ];

    // Non-overflow errors that would otherwise match generic overflow patterns
    // (e.g. Bedrock throttling: "Too many tokens, please wait before trying again.").
    private static readonly Regex[] NonOverflowPatterns =
    [
        new("^(Throttling error|Service unavailable):", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("rate limit", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
        new("too many requests", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
    ];

    /// <summary>Returns whether the error text describes a context-overflow condition.</summary>
    public static bool IsContextOverflow(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
        {
            return false;
        }

        if (NonOverflowPatterns.Any(pattern => pattern.IsMatch(errorText)))
        {
            return false;
        }

        return OverflowPatterns.Any(pattern => pattern.IsMatch(errorText));
    }

    /// <summary>Returns whether any exception in the chain describes a context-overflow condition.</summary>
    public static bool IsContextOverflow(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (IsContextOverflow(current.Message))
            {
                return true;
            }
        }

        return false;
    }
}
