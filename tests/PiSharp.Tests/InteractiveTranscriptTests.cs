using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class InteractiveTranscriptTests
{
    [Fact]
    public void InteractiveTranscriptStreamsAssistantTextAndFiltersTerminalControls()
    {
        using var output = new StringWriter();
        using var status = new StringWriter();
        var transcript = new InteractiveTranscript(output, status);

        transcript.Render(new("model_text_delta", Text: "Hello \u001b[2J界"));
        transcript.Render(new("reasoning_delta", Text: "checking"));
        transcript.FinishTurn();

        Assert.Equal("Hello [2J界" + Environment.NewLine, output.ToString());
        Assert.Equal("checking", status.ToString());
        Assert.False(transcript.HasAssistantOutput);
    }

    [Fact]
    public void SearchToolDetailsRenderLimitAndByteTruncationSummary()
    {
        using var output = new StringWriter();
        using var status = new StringWriter();
        var transcript = new InteractiveTranscript(output, status);
        using var document = JsonDocument.Parse("""
            {
              "matchLimitReached": 200,
              "linesTruncated": true,
              "truncation": {
                "truncated": true,
                "truncatedBy": "bytes",
                "totalBytes": 90000,
                "outputBytes": 50000
              }
            }
            """);

        transcript.Render(new("tool_execution_finished", Tool: "grep", Text: "2 matches", Details: document.RootElement.Clone()));

        Assert.Contains("← 2 matches", status.ToString());
        Assert.Contains("match limit reached (200)", status.ToString());
        Assert.Contains("one or more matching lines were truncated", status.ToString());
        Assert.Contains("output truncated by bytes (50000 of 90000 bytes shown)", status.ToString());
    }

    [Fact]
    public void ToolCallSummariesShowSafeBuiltInArgumentsAndFilterControls()
    {
        using var output = new StringWriter();
        using var status = new StringWriter();
        var transcript = new InteractiveTranscript(output, status);

        transcript.Render(new AgentLifecycleEvent("tool_execution_started", Tool: "bash", OperationId: "call-1")
        {
            ToolArguments = new Dictionary<string, object?> { ["command"] = "git status\u001b[2J\n--short", ["token"] = "private" }
        });
        transcript.Render(new AgentLifecycleEvent("tool_execution_started", Tool: "write")
        {
            ToolArguments = new Dictionary<string, object?> { ["path"] = "notes.txt", ["content"] = "private file content" }
        });

        Assert.Contains("→ bash · command: git status[2J --short (call-1)", status.ToString());
        Assert.Contains("→ write · path: notes.txt", status.ToString());
        Assert.DoesNotContain("private", status.ToString());
        Assert.DoesNotContain('\u001b', status.ToString());
    }

    [Fact]
    public void StructuredSearchRecordsAreSummarizedAndThinkingCanBeHidden()
    {
        using var output = new StringWriter();
        using var status = new StringWriter();
        var transcript = new InteractiveTranscript(output, status, hideThinking: true);

        transcript.Render(new("tool_execution_finished", Tool: "find", IsError: false,
            Details: new SearchDetails(EntryLimitReached: 100)));
        transcript.Render(new("reasoning_delta", Text: "private reasoning"));

        Assert.Contains("entry limit reached (100)", status.ToString());
        Assert.DoesNotContain("private reasoning", status.ToString());
    }

    [Fact]
    public void PrintTranscriptKeepsAssistantOutputOnlyAndReportsAgentFailure()
    {
        using var output = new StringWriter();
        using var status = new StringWriter();
        var transcript = new InteractiveTranscript(output, status, interactive: false);

        transcript.Render(new("model_text_delta", Text: "plain \u001b[31mtext"));
        transcript.Render(new("tool_execution_finished", Tool: "grep", Details: new SearchDetails(ResultLimitReached: 4)));
        transcript.Render(new("turn_failed", Error: "offline"));
        transcript.FinishTurn();

        Assert.Equal("plain \u001b[31mtext" + Environment.NewLine, output.ToString());
        Assert.Equal("Agent error: offline" + Environment.NewLine, status.ToString());
        Assert.Equal(1, transcript.ExitCode);
    }

    [Fact]
    public void ActiveScreenRendersStreamingMarkdownAndCommitsReadableScrollback()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 64, () => 12);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);

        transcript.Render(new("model_text_delta", Text: "# Hea"));
        Assert.Contains("Hea", StripSgr(output.ToString()));
        transcript.Render(new("model_text_delta", Text: "ding\n\n**bold** and `code` [docs](https://example.test) ~~removed~~\nvariable foo_bar\n> quoted\n- item\n- [ ] pending\n- [X] complete\n\n| Name | Count |\n| :--- | ---: |\n| Widget | 2 |\n```csharp\nvar answer = 42;\n```"));
        transcript.FinishTurn();
        screen.Dispose();

        var visible = StripSgr(output.ToString());
        Assert.Contains("Heading", visible);
        Assert.DoesNotContain("# Heading", visible);
        Assert.Contains("bold and code", visible);
        Assert.Contains("docs", visible);
        Assert.Contains("https://example.test", visible);
        Assert.Contains("removed", visible);
        Assert.Contains("foo_bar", visible);
        Assert.Contains("│ quoted", visible);
        Assert.Contains("• item", visible);
        Assert.Contains("• [ ] pending", visible);
        Assert.Contains("• [x] complete", visible);
        Assert.Contains("\u001b[9mremoved\u001b[29m", output.ToString());
        var tableStart = visible.LastIndexOf("┌─", StringComparison.Ordinal);
        var tableEnd = visible.LastIndexOf("└─", StringComparison.Ordinal);
        Assert.True(tableStart >= 0 && tableEnd > tableStart);
        var table = visible[tableStart..tableEnd];
        Assert.Contains("Count", table);
        Assert.Contains("Widget", table);
        Assert.Contains("var answer = 42;", visible);
        Assert.DoesNotContain("```", visible);
    }

    [Fact]
    public void ActiveScreenWrapsGfmTableCellsToAvailableWidth()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 40, () => 30);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
        transcript.Render(new("model_text_delta", Text: "| Name | Description |\n| --- | --- |\n| `A|B` | thisisaverylongunbrokentablecellvalue |\n| Escaped | left\\|right |\n| Trailing | ends\\|"));
        transcript.FinishTurn();
        screen.Dispose();

        var visible = StripSgr(output.ToString());
        var tableStart = visible.LastIndexOf("┌─", StringComparison.Ordinal);
        var tableEnd = visible.LastIndexOf("└─", StringComparison.Ordinal);
        Assert.True(tableStart >= 0 && tableEnd > tableStart);
        var tableLines = visible[tableStart..tableEnd].Split('\n')
            .Where(line => line.StartsWith("┌", StringComparison.Ordinal) || line.StartsWith("├", StringComparison.Ordinal) ||
                line.StartsWith("│", StringComparison.Ordinal));
        Assert.All(tableLines, line => Assert.True(line.Length <= 39, $"Table row exceeded the terminal width: {line}"));
        var tableContent = new string(visible[tableStart..tableEnd].Where(char.IsLetterOrDigit).ToArray());
        Assert.Contains("thisisaverylongunbrokentablecellvalue", tableContent);
        Assert.Contains("A|B", visible[tableStart..tableEnd]);
        Assert.Contains("left|right", visible[tableStart..tableEnd]);
        Assert.Contains("ends|", visible[tableStart..tableEnd]);
    }

    [Fact]
    public void ActiveScreenKeepsGfmSourceWhenUnbrokenTableCellCannotFit()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 12, () => 20);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
        const string markdown = "| A | B | C | D |\n| --- | --- | --- | --- |\n| 界 | 界 | 界 | 界 |";
        transcript.Render(new("model_text_delta", Text: markdown));
        transcript.FinishTurn();
        screen.Dispose();

        var visible = StripSgr(output.ToString());
        Assert.Contains(markdown, visible);
        Assert.DoesNotContain("┌─", visible);
    }

    private static string StripSgr(string text) => Regex.Replace(text, "\\u001b\\[[0-9;]*m", "");

    private sealed record SearchDetails(int? ResultLimitReached = null, int? EntryLimitReached = null);
}
