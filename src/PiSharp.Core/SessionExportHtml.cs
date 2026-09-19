using System.Net;
using System.Text;

namespace PiSharp.Core;

/// <summary>
/// Renders a Pi v3 session as a self-contained, deterministic HTML document
/// (the plain-text port of the pinned themed export page — see docs/PARITY.md for
/// the documented styling difference). All dynamic content is HTML-escaped and the
/// output is stable for identical session content (no export timestamp).
/// </summary>
public static class SessionExportHtml
{
    /// <summary>The default export file name for a session file (pinned &lt;app&gt;-session-&lt;basename&gt;.html).</summary>
    public static string GetDefaultExportPath(string sessionFilePath, string workingDirectory)
    {
        var baseName = Path.GetFileNameWithoutExtension(sessionFilePath);
        return Path.Combine(workingDirectory, $"pisharp-session-{baseName}.html");
    }

    /// <summary>
    /// Exports the document's active branch to <paramref name="outputPath"/> (default: the
    /// pinned &lt;app&gt;-session-&lt;basename&gt;.html name in the process cwd). Pinned guard order:
    /// a document without a session file cannot be exported to HTML at all, and a file that
    /// has not been flushed yet has nothing to export.
    /// </summary>
    public static async Task<string> ExportHtmlAsync(
        SessionDocument document, string? outputPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsPiV3)
        {
            throw new InvalidOperationException("HTML export requires a Pi v3 session.");
        }

        if (string.IsNullOrWhiteSpace(document.FilePath))
        {
            throw new InvalidOperationException(
                "Cannot export in-memory session to HTML. Export as JSONL with /export <path.jsonl> instead.");
        }

        if (!File.Exists(document.FilePath))
        {
            throw new InvalidOperationException("Nothing to export yet - start a conversation first");
        }

        var target = string.IsNullOrWhiteSpace(outputPath)
            ? GetDefaultExportPath(document.FilePath, Environment.CurrentDirectory)
            : outputPath;

        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var html = Render(document);
        await File.WriteAllTextAsync(target, html, Encoding.UTF8, cancellationToken);
        return target;
    }

    /// <summary>Builds the HTML document for the session's active branch.</summary>
    public static string Render(SessionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var branch = document.GetActiveEntryPath(document.LatestEntryId);
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        var piHeader = document.PiHeader!;
        sb.AppendLine($"<title>{Escape(document.Name ?? $"PiSharp Session {piHeader.Id}")}</title>");
        AppendStyle(sb);
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"wrap\">");
        sb.AppendLine("<header>");
        sb.AppendLine($"<h1>{Escape(document.Name ?? "PiSharp Session")}</h1>");
        sb.AppendLine("<p class=\"meta\">");
        sb.AppendLine($"Session {Escape(piHeader.Id)}");
        sb.AppendLine($"<br>Created {Escape(piHeader.Timestamp.ToString("O"))}");
        sb.AppendLine($"<br>Working directory {Escape(piHeader.Cwd)}");
        sb.AppendLine("</p>");
        sb.AppendLine("</header>");
        sb.AppendLine("<main>");

        foreach (var entry in branch)
        {
            if (entry is not { Type: "session" })
            {
                AppendEntry(sb, entry);
            }
        }

        sb.AppendLine("</main>");
        sb.AppendLine("<footer>");
        sb.AppendLine("<p>Exported from PiSharp (pinned session export format, plain rendering).</p>");
        sb.AppendLine("</footer>");
        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    // --- entry projection -----------------------------------------------------------

    private static void AppendEntry(StringBuilder sb, SessionEntry entry)
    {
        var (cssClass, label, body, usage) = Project(entry);
        sb.AppendLine($"<section class=\"entry {cssClass}\">");
        sb.AppendLine($"<p class=\"label\">{label}</p>");
        if (body.Length > 0)
        {
            sb.AppendLine($"<div class=\"body\">{Escape(body)}</div>");
        }

        if (usage.Length > 0)
        {
            sb.AppendLine($"<p class=\"usage\">{Escape(usage)}</p>");
        }

        sb.AppendLine("</section>");
    }

    private static (string Css, string Label, string Body, string Usage) Project(SessionEntry entry)
    {
        switch (entry)
        {
            case MessageEntry message:
            {
                if (!message.Message.TryGetProperty("role", out var roleNode) ||
                    roleNode.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    return ("message", FormatLabel(entry, "message"), "", "");
                }

                var role = roleNode.GetString() ?? "unknown";
                var css = role switch
                {
                    "user" => "user",
                    "assistant" => "assistant",
                    "toolResult" => "tool",
                    _ => "message",
                };
                var label = FormatLabel(entry, $"{entry.Type} \u00b7 {role}");
                string body;
                if (message.Message.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !message.Message.TryGetProperty("content", out var contentElement))
                {
                    body = string.Empty;
                }
                else if (contentElement.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    // User messages commonly carry plain string content (pinned projects both).
                    body = contentElement.GetString() ?? string.Empty;
                }
                else if (contentElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    body = ProjectContent(contentElement);
                }
                else
                {
                    body = string.Empty;
                }

                // Assistant tool calls are rendered after the text.
                var calls = new StringBuilder();
                if (role == "assistant" &&
                    message.Message.TryGetProperty("content", out var contentArray) &&
                    contentArray.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var item in contentArray.EnumerateArray())
                    {
                        if (item.ValueKind != System.Text.Json.JsonValueKind.Object ||
                            !item.TryGetProperty("type", out var typeProp) ||
                            typeProp.GetString() != "toolCall" ||
                            !item.TryGetProperty("name", out var nameProp))
                        {
                            continue;
                        }

                        var args = item.TryGetProperty("arguments", out var argsProp) &&
                                   argsProp.ValueKind == System.Text.Json.JsonValueKind.Object
                            ? argsProp.GetRawText()
                            : "{}";
                        calls.Append($"\n\n[{nameProp.GetString()}]\n{args}");
                    }
                }

                var usage = role == "assistant" &&
                            message.Message.TryGetProperty("usage", out var usageProp) &&
                            usageProp.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? $"tokens {usageProp.GetProperty("totalTokens").GetInt64()} " +
                      $"(in {usageProp.GetProperty("input").GetInt64()}, out {usageProp.GetProperty("output").GetInt64()})" +
                      (usageProp.TryGetProperty("cost", out var cost) &&
                       cost.TryGetProperty("total", out var total)
                          ? $" \u00b7 cost ${total.GetDouble():0.000}"
                          : string.Empty)
                    : string.Empty;

                return (css, label, body + calls.ToString(), usage);
            }

            case CompactionEntry compaction:
                return ("compaction", FormatLabel(entry, "compaction"), compaction.Summary,
                    EntryUsageText(compaction.Usage));

            case BranchSummaryEntry summary:
                return ("compaction", FormatLabel(entry, "branch summary"), summary.Summary,
                    EntryUsageText(summary.Usage));

            case LabelEntry label:
                return ("label", FormatLabel(entry, "label"), label.Label ?? string.Empty, string.Empty);

            case ModelChangeEntry change:
                return ("label", FormatLabel(entry, "model change"),
                    $"\u2192 {change.Provider}/{change.ModelId}", string.Empty);

            case ThinkingLevelChangeEntry thinking:
                return ("label", FormatLabel(entry, "thinking level change"),
                    $"\u2192 {thinking.ThinkingLevel}", string.Empty);

            case SessionInfoEntry info when !string.IsNullOrWhiteSpace(info.Name):
                return ("label", FormatLabel(entry, "name"), $"\u2192 {info.Name}", string.Empty);

            case CustomEntry custom:
            {
                var suffix = string.IsNullOrWhiteSpace(custom.CustomType)
                    ? string.Empty
                    : $" \u00b7 {custom.CustomType}";
                return ("custom", FormatLabel(entry, $"custom{suffix}"),
                    custom.Data?.GetRawText() ?? string.Empty, string.Empty);
            }

            default:
                return ("message", FormatLabel(entry, entry.Type), string.Empty, string.Empty);
        }
    }

    private static string ProjectContent(System.Text.Json.JsonElement content)
    {
        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            var kind = item.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            if (kind is "text" && item.TryGetProperty("text", out var text) &&
                text.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
            else if (kind == "toolCall" && item.TryGetProperty("name", out var name))
            {
                parts.Add($"[toolCall {name.GetString()}]");
            }
            else if ((kind == "thinking" || kind == "redactedThinking") &&
                     item.TryGetProperty("thinking", out var thinking) &&
                     thinking.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                parts.Add($"(thinking) {thinking.GetString()}");
            }
            else if (item.TryGetProperty("text", out var fallbackText) &&
                     fallbackText.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                parts.Add(fallbackText.GetString() ?? string.Empty);
            }
        }

        return string.Join("\n\n", parts);
    }

    private static string EntryUsageText(System.Text.Json.JsonElement? usage)
    {
        if (usage is not { ValueKind: System.Text.Json.JsonValueKind.Object } element)
        {
            return string.Empty;
        }

        var text = $"tokens {ReadLong(element, "totalTokens")} " +
                   $"(in {ReadLong(element, "input")}, out {ReadLong(element, "output")})";
        if (element.TryGetProperty("cost", out var cost) &&
            cost.TryGetProperty("total", out var total))
        {
            text += $" \u00b7 cost ${total.GetDouble():0.000}";
        }

        return text;
    }

    private static long ReadLong(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        (value.ValueKind == System.Text.Json.JsonValueKind.Number ||
         value.ValueKind == System.Text.Json.JsonValueKind.Null)
            ? value.TryGetInt64(out var parsed) ? parsed : 0L
            : 0L;

    private static string FormatLabel(SessionEntry entry, string description) =>
        $"{entry.Timestamp.UtcDateTime:yyyy-MM-dd HH:mm:ss} \u00b7 {description}";

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    private static void AppendStyle(StringBuilder sb)
    {
        sb.AppendLine("<style>");
        sb.AppendLine(":root { color-scheme: dark; }");
        sb.AppendLine("body { margin: 0; background: #111; color: #e5e5e5; font-family: ui-sans-serif, system-ui, sans-serif; line-height: 1.5; }");
        sb.AppendLine(".wrap { max-width: 860px; margin: 0 auto; padding: 2rem 1rem; }");
        sb.AppendLine("h1 { font-size: 1.4rem; margin: 0 0 .25rem; }");
        sb.AppendLine(".meta { color: #8b8b8b; font-size: .85rem; margin: 0; }");
        sb.AppendLine(".entry { border: 1px solid #2a2a2a; border-radius: 8px; margin: 1rem 0; padding: .75rem 1rem; }");
        sb.AppendLine(".label { font-size: .72rem; text-transform: uppercase; letter-spacing: .04em; color: #8b8b8b; margin: 0 0 .5rem; }");
        sb.AppendLine(".user .label { color: #4da3ff; }");
        sb.AppendLine(".assistant .label { color: #3fb950; }");
        sb.AppendLine(".tool .label { color: #d29922; }");
        sb.AppendLine(".compaction .label, .custom .label, .label .label { color: #a371f7; }");
        sb.AppendLine(".body { white-space: pre-wrap; word-wrap: break-word; }");
        sb.AppendLine(".usage { color: #777; font-size: .75rem; margin: .5rem 0 0; }");
        sb.AppendLine("footer { margin-top: 2rem; color: #666; font-size: .8rem; }");
        sb.AppendLine("</style>");
    }
}
