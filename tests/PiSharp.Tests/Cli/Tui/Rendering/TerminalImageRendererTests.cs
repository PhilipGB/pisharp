using System.Text;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Tui;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class TerminalImageRendererTests
{
    [Fact]
    public void DetectsCurrentPiTerminalProtocolsAndKeepsMultiplexersConservative()
    {
        Assert.Equal(TerminalImageProtocol.Kitty,
            new TerminalImageRenderer(Env(("KITTY_WINDOW_ID", "2"))).Protocol);
        Assert.Equal(TerminalImageProtocol.Kitty,
            new TerminalImageRenderer(Env(("TERM_PROGRAM", "ghostty"))).Protocol);
        Assert.Equal(TerminalImageProtocol.ITerm2,
            new TerminalImageRenderer(Env(("TERM_PROGRAM", "iTerm.app"))).Protocol);
        Assert.Equal(TerminalImageProtocol.None,
            new TerminalImageRenderer(Env(("TERM_PROGRAM", "kitty"), ("TMUX", "1"))).Protocol);
        Assert.Equal(TerminalImageProtocol.None,
            new TerminalImageRenderer(Env(("TERM", "screen-256color"))).Protocol);
        Assert.Equal(TerminalImageProtocol.Kitty,
            new TerminalImageRenderer(Env(("TMUX", "1"), ("PI_IMAGE_PROTOCOL", "kitty"))).Protocol);
        Assert.Equal(TerminalImageProtocol.None,
            new TerminalImageRenderer(Env(("KITTY_WINDOW_ID", "2"), ("PI_IMAGE_PROTOCOL", "none"))).Protocol);
    }

    [Fact]
    public void DetectsOsc8HyperlinksUsingPinnedPiTerminalRules()
    {
        Assert.True(TerminalImageCapabilities.SupportsHyperlinks(Env(("TERM_PROGRAM", "kitty"))));
        Assert.True(TerminalImageCapabilities.SupportsHyperlinks(Env(("WT_SESSION", "1"))));
        Assert.False(TerminalImageCapabilities.SupportsHyperlinks(Env(("TERM", "screen-256color"))));
        Assert.False(TerminalImageCapabilities.SupportsHyperlinks(Env(("TERMINAL_EMULATOR", "JetBrains-JediTerm"))));
        Assert.True(TerminalImageCapabilities.SupportsHyperlinks(Env(("TMUX", "1"), ("PI_HYPERLINKS", "1")),
            () => false));
        Assert.False(TerminalImageCapabilities.SupportsHyperlinks(Env(("TMUX", "1"), ("TERM_PROGRAM", "kitty")),
            () => false));
        Assert.True(TerminalImageCapabilities.SupportsHyperlinks(Env(("TMUX", "1")), () => true));
    }

    [Fact]
    public void LayoutUsesTerminalWidthHeightAndAspectPreservingCellRows()
    {
        var renderer = new TerminalImageRenderer(Env(("KITTY_WINDOW_ID", "2")));
        var marker = renderer.Register(CreateImage(10, 40), out _)!;

        var wide = renderer.LayoutTranscript(marker, terminalColumns: 80, maximumImageRows: 12).Split('\n');
        var narrow = renderer.LayoutTranscript(marker, terminalColumns: 22, maximumImageRows: 6).Split('\n');

        Assert.Equal(12, wide.Length);
        Assert.Equal(6, narrow.Length);
        Assert.All(narrow, row => Assert.True(TerminalImageRenderer.TryReadMarker(row, 0, out _, out _, out _)));
        var frame = renderer.PrepareFrame(narrow);
        Assert.Contains("c=3,r=6", frame.Rows[0]);
        Assert.Contains("a=T", frame.Rows[0]);
        Assert.DoesNotContain("psimg-", string.Join('\n', frame.Rows));
        Assert.Equal(0, TerminalTextLayout.Width(narrow[0]));
        Assert.Single(TerminalTextLayout.Wrap(narrow[0], 1));
        var tallViewport = renderer.LayoutTranscript(marker, terminalColumns: 22, maximumImageRows: 100).Split('\n');
        Assert.Equal(10, tallViewport.Length);
    }

    [Fact]
    public void CellSizingMatchesCurrentPiKittyAndItermDistortionCases()
    {
        var kittyDefault = TerminalImageLayout.Fit(new(615, 86), 60, 1000, new(9, 18), reduceCellDistortion: true);
        var kittyNarrow = TerminalImageLayout.Fit(new(615, 86), 30, 1000, new(9, 18), reduceCellDistortion: true);
        var kittyTallCells = TerminalImageLayout.Fit(new(615, 86), 60, 1000, new(15, 28), reduceCellDistortion: true);
        var kittyThin = TerminalImageLayout.Fit(new(1200, 12), 60, 1000, new(9, 18), reduceCellDistortion: true);
        var itermHeightLimited = TerminalImageLayout.Fit(new(400, 900), 30, 15, new(14, 28), reduceCellDistortion: false);

        Assert.Equal(new TerminalImageCells(60, 4), kittyDefault);
        Assert.Equal(new TerminalImageCells(30, 2), kittyNarrow);
        Assert.Equal(new TerminalImageCells(60, 5), kittyTallCells);
        Assert.Equal(new TerminalImageCells(60, 1), kittyThin);
        Assert.Equal(new TerminalImageCells(14, 15), itermHeightLimited);
    }

    [Fact]
    public void KittyRedrawReusesUnchangedPlacementAndPartialViewportUsesTextFallback()
    {
        var renderer = new TerminalImageRenderer(Env(("KITTY_WINDOW_ID", "2")));
        var marker = renderer.Register(CreateImage(10, 40), out var fallback)!;
        var rows = renderer.LayoutTranscript(marker, terminalColumns: 22, maximumImageRows: 6).Split('\n');
        var first = renderer.PrepareFrame(rows);
        var redraw = renderer.PrepareFrame(rows);
        var movedRows = rows.Select(line => "prefix" + line).ToArray();
        var moved = renderer.PrepareFrame(movedRows);
        var partial = renderer.PrepareFrame(movedRows.Take(2).ToArray());

        Assert.Contains("a=T", first.Rows[0]);
        Assert.Empty(redraw.Preamble);
        Assert.Contains("a=p", redraw.Rows[0]);
        Assert.DoesNotContain("AAAA", redraw.Rows[0]);
        Assert.Contains("a=d,d=a", moved.Preamble);
        Assert.Contains("a=p", moved.Rows[0]);
        Assert.Contains(fallback, partial.Rows[0]);
        Assert.Contains("a=d,d=a", partial.Preamble);
        Assert.DoesNotContain("\u001b_G", string.Join('\n', partial.Rows));
        Assert.Contains("a=d,d=A", renderer.CleanupControlSequence());
    }

    [Fact]
    public void KittyCompositorDoesNotReemitUnchangedImageOnEveryFrame()
    {
        using var output = new StringWriter();
        var renderer = new TerminalImageRenderer(Env(("KITTY_WINDOW_ID", "2")));
        var marker = renderer.Register(CreateImage(1, 1), out _)!;
        var rows = renderer.LayoutTranscript(marker, terminalColumns: 22, maximumImageRows: 6).Split('\n');
        var frame = new TerminalScreenCompositor.Frame(rows, CursorRow: 0, CursorColumn: 1,
            ScrollOffset: 0, Columns: 22, Height: 6);
        var compositor = new TerminalScreenCompositor(output, renderer);

        compositor.Render(frame);
        output.GetStringBuilder().Clear();
        compositor.Render(frame);
        output.GetStringBuilder().Clear();
        compositor.Render(frame);

        Assert.DoesNotContain("\u001b_G", output.ToString());

        _ = renderer.HidePlacements();
        compositor.InvalidateRenderedFrame();
        output.GetStringBuilder().Clear();
        compositor.Render(frame);
        Assert.Contains("a=p", output.ToString());
    }

    [Fact]
    public void Iterm2DrawsAtTheEndOfReservedRowsAndRestoresCursorAccounting()
    {
        var renderer = new TerminalImageRenderer(Env(("TERM_PROGRAM", "iTerm.app")));
        var image = CreateImage(10, 40);
        var marker = renderer.Register(image, out _)!;
        var rows = renderer.LayoutTranscript(marker, terminalColumns: 22, maximumImageRows: 6).Split('\n');
        var frame = renderer.PrepareFrame(rows);

        Assert.Equal(6, frame.Rows.Count);
        Assert.Equal("", frame.Rows[0]);
        Assert.Equal("", frame.Rows[4]);
        Assert.StartsWith("\u001b[5A\u001b]1337;File=inline=1;size=", frame.Rows[5]);
        Assert.Contains("width=3;height=auto:", frame.Rows[5]);
        Assert.DoesNotContain("\u001b_G", string.Join('\n', frame.Rows));
    }

    [Fact]
    public void CompositorReanchorsFollowingRowsAfterInlineImages()
    {
        using var output = new StringWriter();
        var renderer = new TerminalImageRenderer(Env(("TERM_PROGRAM", "iTerm.app")));
        var marker = renderer.Register(CreateImage(10, 40), out _)!;
        var rows = renderer.LayoutTranscript(marker, terminalColumns: 22, maximumImageRows: 6).Split('\n').ToList();
        rows.Add("after image");
        var compositor = new TerminalScreenCompositor(output, renderer);

        compositor.Render(new(rows, CursorRow: 6, CursorColumn: 0, ScrollOffset: 0, Columns: 22, Height: 7));

        var rendered = output.ToString();
        var imageEnd = rendered.IndexOf('\u0007');
        var followingRow = rendered.IndexOf("\u001b[7;1H\u001b[2Kafter image", imageEnd, StringComparison.Ordinal);
        Assert.True(imageEnd >= 0 && followingRow > imageEnd);
        Assert.DoesNotContain("\u001b[K", rendered);
    }

    [Fact]
    public void PruningRetainedTranscriptImagesFreesCacheAndDeletesUploadedKittyData()
    {
        var renderer = new TerminalImageRenderer(Env(("KITTY_WINDOW_ID", "2")));
        var markers = new string?[64];
        for (var index = 0; index < markers.Length; index++)
            markers[index] = renderer.Register(CreateImage(1, 1), out _);
        Assert.All(markers, marker => Assert.NotNull(marker));

        var firstLayout = renderer.LayoutTranscript(markers[0]!, terminalColumns: 22, maximumImageRows: 2).Split('\n');
        _ = renderer.PrepareFrame(firstLayout);
        renderer.PruneUnreferenced(string.Empty);

        var next = renderer.Register(CreateImage(1, 1), out _);
        Assert.NotNull(next);
        var nextLayout = renderer.LayoutTranscript(next!, terminalColumns: 22, maximumImageRows: 2).Split('\n');
        var nextFrame = renderer.PrepareFrame(nextLayout);

        Assert.Contains("a=d,d=I,i=", nextFrame.Preamble);
        Assert.Contains("a=T", nextFrame.Rows[0]);
    }

    [Fact]
    public void UnsupportedOrInvalidImagesProduceOnlySafeVisibleFallbacks()
    {
        var renderer = new TerminalImageRenderer(Env(("TERM", "xterm-256color")));
        var marker = renderer.Register(CreateImage(12, 8), out var fallback);
        var invalidMarker = renderer.Register(new DataContent(new byte[] { 1, 2, 3 }, "image/png"), out var invalidFallback);

        Assert.Null(marker);
        Assert.Null(invalidMarker);
        Assert.Contains("invalid", invalidFallback, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fallback, renderer.LayoutTranscript(fallback, terminalColumns: 80, maximumImageRows: 20));
        Assert.DoesNotContain('\u001b', fallback);
    }

    private static DataContent CreateImage(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return new DataContent(encoded.ToArray(), "image/png");
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }
}
