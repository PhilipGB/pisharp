using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Private, standalone HTML export of the entire canonical tree, including inactive branches.</summary>
public static class SessionExport
{
    public static async Task ExportHtmlAsync(ConversationSession session, string path,
        CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        if (File.Exists(target)) throw new IOException("Export destination already exists; choose a new path.");
        var temp = Path.Combine(folder, ".pisharp-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
            await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                async Task Line(string html) => await writer.WriteLineAsync(html.AsMemory(), cancellationToken);
                static string Html(string? value) => HtmlEncoder.Default.Encode(value ?? "");
                await Line("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>PiSharp session</title>");
                await Line("<style>body{max-width:70rem;margin:2rem auto;padding:0 1rem;font:1rem system-ui;background:#17191e;color:#e0e0e0}article{border:1px solid #666;padding:1rem;margin:1rem 0}pre{white-space:pre-wrap;overflow-wrap:anywhere}small{color:#aaa}</style>");
                await Line($"<h1>{Html(session.Name ?? "PiSharp session")}</h1><p>Session {Html(session.Id)} · {Html(session.Model)} · {Html(session.WorkingDirectory)}</p>");
                await Line("<p>This file may contain confidential prompts, tool arguments and results. Do not share it without reviewing.</p>");
                foreach (var node in session.Tree.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Line($"<article><small>{Html(node.Type)} · {Html(node.Id)} · parent {Html(node.ParentId ?? "root")} · {Html(node.Timestamp.ToString("O"))}{(node.Id == session.Tree.HeadId ? " · selected" : "")}</small>");
                    if (node.Type == "chat")
                    {
                        var message = ConversationSession.RestoreEntry(node);
                        await Line($"<h2>{Html(message.Role.ToString())}</h2>");
                        foreach (var content in message.Contents)
                        {
                            var value = content is TextContent text ? text.Text :
                                JsonSerializer.Serialize(content, AIJsonUtilities.DefaultOptions);
                            await Line($"<pre>{Html(value)}</pre>");
                            var failure = content switch
                            {
                                FunctionCallContent { Exception: not null } call => call.Exception.Message,
                                FunctionResultContent { Exception: not null } result => result.Exception.Message,
                                _ => null
                            };
                            if (failure is not null) await Line($"<strong>Tool failure:</strong><pre>{Html(failure)}</pre>");
                        }
                    }
                    else if (node.Type == "bash_execution")
                    {
                        await Line("<h2>Bash execution</h2>");
                        await Line($"<p><strong>Command:</strong> {Html(node.Payload.GetProperty("command").GetString())}</p>");
                        await Line($"<pre>{Html(node.Payload.GetProperty("output").GetString())}</pre>");
                        var exitCode = node.Payload.GetProperty("exitCode");
                        var fullOutputPath = node.Payload.GetProperty("fullOutputPath");
                        await Line($"<small>Exit code: {Html(exitCode.ValueKind == JsonValueKind.Null ? "not reported" : exitCode.ToString())} · " +
                            $"Cancelled: {Html(node.Payload.GetProperty("cancelled").GetBoolean().ToString())} · " +
                            $"Truncated: {Html(node.Payload.GetProperty("truncated").GetBoolean().ToString())} · " +
                            $"Excluded from context: {Html(node.Payload.GetProperty("excludeFromContext").GetBoolean().ToString())}" +
                            (fullOutputPath.ValueKind == JsonValueKind.String ? $" · Full output: {Html(fullOutputPath.GetString())}" : "") + "</small>");
                    }
                    else if (node.Type == "compaction")
                        await Line($"<h2>Context summary</h2><pre>{Html(node.Payload.GetProperty("summary").GetString())}</pre>");
                    else if (node.Type == "run_recovered")
                        await Line($"<h2>Recovered interrupted run</h2><pre>{Html(node.Payload.ToString())}</pre>");
                    await Line("</article>");
                }
                await Line("</html>");
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, target); // Never overwrite an unrelated user file or follow a destination symlink.
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
