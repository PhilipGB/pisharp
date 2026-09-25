using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class SessionExportTests
{
    [Fact]
    public async Task HtmlExportEscapesUntrustedContentAndIncludesInactiveBranchesAndFailures()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var conversation = new ConversationSession(cwd, "fixture", null);
            conversation.Rename("<img src=x onerror=alert(1)>");
            conversation.Append(new ChatMessage(ChatRole.User, "<script>alert('secret')</script>"));
            var parent = conversation.Tree.HeadId;
            conversation.Append(new ChatMessage(ChatRole.Assistant, "branch-a"));
            conversation.Tree.Select(parent);
            conversation.Append(new ChatMessage(ChatRole.Assistant, "branch-b"));
            conversation.Append(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent("x", "failed result") { Exception = new ToolFailureException("boom <bad>") }]));
            conversation.AppendBashExecution(new BashExecutionRecord("printf <secret>", "<script>bash-output</script>",
                7, false, true, "/tmp/full-output.log", true));
            var target = Path.Combine(cwd, "export.html");
            await SessionExport.ExportHtmlAsync(conversation, target);
            var html = await File.ReadAllTextAsync(target);
            Assert.Contains("&lt;script&gt;", html);
            Assert.DoesNotContain("<script>alert", html);
            Assert.DoesNotContain("<img src=x", html);
            Assert.Contains("branch-a", html);
            Assert.Contains("branch-b", html);
            Assert.Contains("Tool failure:", html);
            Assert.Contains("boom &lt;bad&gt;", html);
            Assert.Contains("Bash execution", html);
            Assert.Contains("printf &lt;secret&gt;", html);
            Assert.Contains("&lt;script&gt;bash-output&lt;/script&gt;", html);
            Assert.Contains("Excluded from context: True", html);
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
            await Assert.ThrowsAsync<IOException>(() => SessionExport.ExportHtmlAsync(conversation, target));
            Assert.Equal(html, await File.ReadAllTextAsync(target));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task AbortedExportDoesNotCreateOrOverwriteDestination()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var target = Path.Combine(cwd, "cancelled.html");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SessionExport.ExportHtmlAsync(
                new ConversationSession(cwd, "fixture", null), target, cancel.Token));
            Assert.False(File.Exists(target));
            Assert.Empty(Directory.EnumerateFiles(cwd, ".pisharp-export-*.tmp"));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }
}
