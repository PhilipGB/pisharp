using System.Diagnostics;
using System.Text;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalColorQueryControllerTests
{
    [Fact]
    public void SendsOneDa1BoundedQueryAndCollectsTheWholePalette()
    {
        using var output = new StringWriter();
        var changes = new List<TerminalColorState>();
        using var query = new TerminalColorQueryController(output, changes.Add, TimeSpan.FromSeconds(2));

        query.Start(queryColors: true, followAppearance: true);

        var expected = new StringBuilder("\u001b[?2031h\u001b]10;?\u0007\u001b]11;?\u0007");
        for (var index = 0; index < 16; index++) expected.Append("\u001b]4;").Append(index).Append(";?\u0007");
        expected.Append("\u001b[c");
        Assert.Equal(expected.ToString(), output.ToString());

        Send(query, "10;#f8f8f2");
        Send(query, "11;#282a36");
        for (var index = 0; index < 16; index++) Send(query, $"4;{index};#{index:x2}{index:x2}{index:x2}");

        Assert.Equal(new TerminalTheme.Rgb(248, 248, 242), query.Current.Foreground);
        Assert.Equal(new TerminalTheme.Rgb(40, 42, 54), query.Current.Background);
        Assert.True(query.Current.Palette?.Count == 16);
        Assert.Equal(new TerminalTheme.Rgb(15, 15, 15), query.Current.Palette?[15]);
        Assert.Single(changes);

        query.HandleDeviceAttributes();
        Assert.Equal(expected.ToString(), output.ToString());
    }

    [Fact]
    public void Da1CompletesPartialAndEmptyRepliesWithoutInventingPaletteColors()
    {
        using var output = new StringWriter();
        var changes = new List<TerminalColorState>();
        using var query = new TerminalColorQueryController(output, changes.Add, TimeSpan.FromSeconds(2));
        query.Start(queryColors: true, followAppearance: false);

        Send(query, "11;#ffffff");
        query.HandleDeviceAttributes();
        Assert.Equal(new TerminalTheme.Rgb(255, 255, 255), query.Current.Background);
        Assert.Null(query.Current.Palette);

        query.HandleDeviceAttributes();
        Assert.Null(query.Current.Foreground);
        Assert.Single(changes);
    }

    [Fact]
    public async Task TimeoutDoesNotBlockStartupAndLateRepliesAreAppliedAtDa1()
    {
        using var output = new StringWriter();
        var changes = new List<TerminalColorState>();
        using var query = new TerminalColorQueryController(output, changes.Add, TimeSpan.FromMilliseconds(20));
        var stopwatch = Stopwatch.StartNew();
        query.Start(queryColors: true, followAppearance: false);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));

        await Task.Delay(60);
        Send(query, "10;#ffffff");
        query.HandleDeviceAttributes();

        Assert.Equal(new TerminalTheme.Rgb(255, 255, 255), query.Current.Foreground);
        Assert.Single(changes);
    }

    [Fact]
    public void AppearanceReportsRefreshColorsAndToggleMode2031()
    {
        using var output = new StringWriter();
        var changes = new List<TerminalColorState>();
        using var query = new TerminalColorQueryController(output, changes.Add, TimeSpan.FromSeconds(2));
        query.Start(queryColors: true, followAppearance: true);

        query.HandleAppearanceReport("light");
        query.SetFollowAppearance(false);

        Assert.Equal("light", query.Current.AppearanceReport);
        Assert.Equal(2, Count(output.ToString(), "\u001b]10;?"));
        Assert.Contains("\u001b[?2031h", output.ToString());
        Assert.Contains("\u001b[?2031l", output.ToString());
        Assert.Single(changes);
    }

    [Fact]
    public void ShutdownCompletionConsumesRepliesThroughDaAndDoesNotConsumeFollowingInput()
    {
        using var output = new StringWriter();
        using var query = new TerminalColorQueryController(output, _ => { });
        var input = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b]11;#282a36\a\u001b[?62;22cX")));
        input.TerminalColorReceived += query.HandleColorResponse;
        input.TerminalDeviceAttributesReceived += query.HandleDeviceAttributes;
        query.Start(queryColors: true, followAppearance: true);
        query.CompletePendingReplies(input);
        Assert.False(query.HasPendingReplies);
        Assert.Contains("\u001b[?2031l", output.ToString());
        Assert.Equal('X', input.Read().Key?.KeyChar);
    }

    private static void Send(TerminalColorQueryController query, string payload)
    {
        Assert.True(TerminalColorResponse.TryParse(payload, out var response), payload);
        query.HandleColorResponse(response);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }
}
