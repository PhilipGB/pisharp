namespace PiSharp.Cli.Tui;

internal readonly record struct TerminalImagePixels(int Width, int Height);
internal readonly record struct TerminalCellPixels(int Width, int Height);
internal readonly record struct TerminalImageCells(int Columns, int Rows);

/// <summary>Fits image pixels into terminal cells while respecting cell aspect ratio and viewport limits.</summary>
internal static class TerminalImageLayout
{
    public static TerminalImageCells Fit(TerminalImagePixels image, int maximumColumns, int maximumRows,
        TerminalCellPixels cell, bool reduceCellDistortion)
    {
        var maxColumns = Math.Max(1, maximumColumns);
        var maxRows = Math.Max(1, maximumRows);
        var imageWidth = Math.Max(1, image.Width);
        var imageHeight = Math.Max(1, image.Height);
        var cellWidth = Math.Max(1, cell.Width);
        var cellHeight = Math.Max(1, cell.Height);
        var widthScale = (maxColumns * (double)cellWidth) / imageWidth;
        var heightScale = (maxRows * (double)cellHeight) / imageHeight;
        var scale = Math.Min(widthScale, heightScale);
        var scaledWidth = imageWidth * scale;
        var scaledHeight = imageHeight * scale;
        var columns = Math.Clamp((int)Math.Ceiling(scaledWidth / cellWidth), 1, maxColumns);
        var rows = Math.Clamp((int)Math.Ceiling(scaledHeight / cellHeight), 1, maxRows);
        if (!reduceCellDistortion) return new(columns, rows);

        if (widthScale <= heightScale)
        {
            var idealRows = columns * (double)cellWidth * imageHeight / (imageWidth * cellHeight);
            rows = ChooseLessDistorted(rows, idealRows);
        }
        else
        {
            var idealColumns = rows * (double)cellHeight * imageWidth / (imageHeight * cellWidth);
            columns = ChooseLessDistorted(columns, idealColumns);
        }
        return new(columns, rows);
    }

    private static int ChooseLessDistorted(int upper, double ideal)
    {
        if (upper <= 1) return upper;
        var lower = upper - 1;
        var upperDistortion = Math.Max(upper / ideal, ideal / upper);
        var lowerDistortion = Math.Max(lower / ideal, ideal / lower);
        return lowerDistortion < upperDistortion ? lower : upper;
    }
}
