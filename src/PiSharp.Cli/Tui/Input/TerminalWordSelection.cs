using System.Globalization;
using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Finds terminal word spans without splitting display graphemes or treating ANSI as text.</summary>
internal static class TerminalWordSelection
{
    private readonly record struct Segment(int Start, int End, bool Word, bool Joiner, bool Whitespace);

    public static (int Start, int End)? RangeAt(string source, int cell)
    {
        var text = TerminalTextLayout.StripFormatting(source);
        var segments = new List<Segment>();
        var column = 0;
        for (var offset = 0; offset < text.Length;)
        {
            var element = StringInfo.GetNextTextElement(text, offset);
            var width = TerminalTextLayout.Width(element);
            var word = element.EnumerateRunes().Any(rune => Rune.IsLetterOrDigit(rune) || rune.Value == '_');
            var joiner = element is "/" or "-";
            var whitespace = string.IsNullOrWhiteSpace(element);
            if (segments.Count > 0 && (word && segments[^1].Word || whitespace && segments[^1].Whitespace))
                segments[^1] = segments[^1] with { End = column + width };
            else
                segments.Add(new(column, column + width, word, joiner, whitespace));
            column += width;
            offset += element.Length;
        }

        // Preserve the internal punctuation in the pinned Latin-word and source-path vectors.
        for (var index = 1; index + 1 < segments.Count;)
        {
            var punctuation = TerminalTextLayout.SliceCells(text, segments[index].Start, segments[index].End);
            if (segments[index - 1].Word && segments[index + 1].Word && punctuation is "." or ":" or "'" or "’")
            {
                segments[index - 1] = segments[index - 1] with { End = segments[index + 1].End };
                segments.RemoveRange(index, 2);
            }
            else index++;
        }

        var clicked = segments.FindIndex(segment => cell >= segment.Start && cell < segment.End);
        if (clicked < 0) return null;
        var first = clicked;
        var last = clicked;
        while (first > 0 && CanJoin(segments[first - 1], segments[first])) first--;
        while (last + 1 < segments.Count && CanJoin(segments[last], segments[last + 1])) last++;
        return (segments[first].Start, segments[last].End);
    }

    private static bool CanJoin(Segment left, Segment right) =>
        (left.Word || left.Joiner) && (right.Word || right.Joiner) && (left.Joiner || right.Joiner);
}
