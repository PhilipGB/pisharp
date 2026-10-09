using System.Globalization;
using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Measures and wraps terminal text by display cells while preserving supported styling.</summary>
internal static class TerminalTextLayout
{
    private const string SgrReset = "\u001b[0m";
    private const string SelectionOn = "\u001b[7m";
    private const string SelectionOff = "\u001b[27m";

    public static int Width(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var maximum = 0;
        var current = 0;
        for (var offset = 0; offset < text.Length;)
        {
            if (TerminalImageRenderer.TryReadMarker(text, offset, out var markerLength, out _, out _))
            {
                offset += markerLength;
                continue;
            }
            if (TryReadEscape(text, offset, out var length, out _, out _))
            {
                offset += length;
                continue;
            }

            if (text[offset] is '\r' or '\n')
            {
                maximum = Math.Max(maximum, current);
                current = 0;
                offset += text[offset] == '\r' && offset + 1 < text.Length && text[offset + 1] == '\n' ? 2 : 1;
                continue;
            }

            if (text[offset] == '\t')
            {
                current += 4 - current % 4;
                offset++;
                continue;
            }

            var element = StringInfo.GetNextTextElement(text, offset);
            var safe = Sanitize(element);
            current += TerminalCells.Width(safe);
            offset += element.Length;
        }
        return Math.Max(maximum, current);
    }

    public sealed class LayoutResult
    {
        private readonly int[] _rowStarts;
        private readonly int _sourceLength;

        internal LayoutResult(IReadOnlyList<string> rows, int[] rowStarts, int sourceLength)
        {
            Rows = rows;
            _rowStarts = rowStarts;
            _sourceLength = sourceLength;
        }

        public IReadOnlyList<string> Rows { get; }
        public int RowCount => Rows.Count;

        public int VisualRowAt(int sourceIndex)
        {
            sourceIndex = Math.Clamp(sourceIndex, 0, _sourceLength);
            var low = 0;
            var high = _rowStarts.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_rowStarts[middle] <= sourceIndex) low = middle + 1;
                else high = middle;
            }
            return Math.Clamp(low - 1, 0, Rows.Count - 1);
        }
    }

    public static IReadOnlyList<string> Wrap(string text, int width) => Create(text, width).Rows;

    public static LayoutResult Create(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        if (text.Length == 0) return new([""], [0], 0);

        var tokens = Tokenize(text);
        var rows = new List<string>();
        var rowStarts = new List<int> { 0 };
        var line = new StringBuilder();
        var activeSgr = new List<string>();
        string? activeHyperlinkOpen = null;
        string? activeHyperlinkClose = null;
        var used = 0;
        var hasContent = false;
        var pendingWhitespace = new List<WrapToken>();

        void StartLine()
        {
            foreach (var sequence in activeSgr) line.Append(sequence);
            if (activeHyperlinkOpen is not null) line.Append(activeHyperlinkOpen);
        }

        void FinishLine(int nextSourceIndex)
        {
            if (activeHyperlinkClose is not null) line.Append(activeHyperlinkClose);
            line.Append(SgrReset);
            rows.Add(line.ToString());
            line.Clear();
            rowStarts.Add(nextSourceIndex);
            used = 0;
            hasContent = false;
        }

        void ProcessState(string value)
        {
            for (var offset = 0; offset < value.Length;)
            {
                if (!TryReadEscape(value, offset, out var length, out var kind, out var payload))
                {
                    offset++;
                    continue;
                }
                var sequence = value.Substring(offset, length);
                if (kind == EscapeKind.Sgr)
                {
                    if (ResetsSgr(payload)) activeSgr.Clear();
                    if (payload.Length > 0 && payload != "0") activeSgr.Add(sequence);
                }
                else if (kind == EscapeKind.Hyperlink)
                {
                    if (HyperlinkTarget(payload).Length == 0)
                    {
                        activeHyperlinkOpen = null;
                        activeHyperlinkClose = null;
                    }
                    else
                    {
                        activeHyperlinkOpen = sequence;
                        var terminator = sequence.EndsWith('\a') ? "\a" : "\u001b\\";
                        activeHyperlinkClose = "\u001b]8;;" + terminator;
                    }
                }
                offset += length;
            }
        }

        void AppendControl(string value)
        {
            line.Append(value);
            ProcessState(value);
        }

        void AppendAtom(WrapAtom atom)
        {
            AppendControl(atom.Prefix);
            if (atom.Kind == WrapKind.Tab)
            {
                var spaces = 4 - used % 4;
                line.Append(' ', spaces);
                used += spaces;
                hasContent = true;
            }
            else if (atom.Kind == WrapKind.Marker)
            {
                line.Append(atom.Content);
                hasContent = true;
            }
            else
            {
                var content = atom.Content;
                var cells = atom.Width;
                if (cells > width)
                {
                    content = "?";
                    cells = 1;
                }
                line.Append(content);
                used += cells;
                hasContent = true;
            }
            AppendControl(atom.Suffix);
        }

        void AppendToken(WrapToken token)
        {
            foreach (var atom in token.Atoms) AppendAtom(atom);
        }

        void ProcessSkippedWhitespace()
        {
            foreach (var token in pendingWhitespace)
                foreach (var atom in token.Atoms)
                {
                    ProcessState(atom.Prefix);
                    ProcessState(atom.Suffix);
                }
            pendingWhitespace.Clear();
        }

        int PendingWhitespaceWidth()
        {
            var column = used;
            foreach (var token in pendingWhitespace)
                foreach (var atom in token.Atoms)
                    column += atom.Kind == WrapKind.Tab ? 4 - column % 4 : atom.Width;
            return column - used;
        }

        foreach (var token in tokens)
        {
            if (token.Kind is WrapKind.Whitespace or WrapKind.Tab)
            {
                pendingWhitespace.Add(token);
                continue;
            }

            if (token.Kind == WrapKind.Newline)
            {
                var newline = token.Atoms[0];
                FinishLine(newline.SourceEnd);
                ProcessSkippedWhitespace();
                ProcessState(newline.Prefix);
                ProcessState(newline.Suffix);
                StartLine();
                continue;
            }

            if (token.Kind == WrapKind.Control)
            {
                AppendToken(token);
                continue;
            }

            var pendingWidth = PendingWhitespaceWidth();
            var overlongWord = token.Kind == WrapKind.Word && token.Width > width;
            if (overlongWord)
            {
                if (hasContent && pendingWhitespace.Count > 0)
                {
                    FinishLine(token.SourceStart);
                    ProcessSkippedWhitespace();
                    StartLine();
                }
                else if (!hasContent && pendingWhitespace.Count > 0 &&
                         pendingWidth + (token.Atoms.FirstOrDefault(atom => atom.Width > 0)?.Width ?? 0) > width)
                {
                    ProcessSkippedWhitespace();
                    StartLine();
                }
                else
                {
                    foreach (var whitespace in pendingWhitespace) AppendToken(whitespace);
                    pendingWhitespace.Clear();
                }
            }
            else if (used + pendingWidth + token.Width > width)
            {
                if (hasContent)
                {
                    FinishLine(token.SourceStart);
                    ProcessSkippedWhitespace();
                    StartLine();
                }
                else
                {
                    ProcessSkippedWhitespace();
                    StartLine();
                }
            }
            else
            {
                foreach (var whitespace in pendingWhitespace) AppendToken(whitespace);
                pendingWhitespace.Clear();
            }

            if (token.Kind == WrapKind.Word && token.Width > width)
            {
                foreach (var atom in token.Atoms)
                {
                    if (atom.Width <= width && hasContent && used + atom.Width > width)
                    {
                        FinishLine(atom.SourceStart);
                        StartLine();
                    }
                    AppendAtom(atom);
                }
            }
            else
            {
                AppendToken(token);
            }
        }

        // Wrapping whitespace is deliberately omitted at the end, as it is before a soft wrap.
        if (line.Length > 0 || rows.Count == 0 || text[^1] is '\r' or '\n')
        {
            if (activeHyperlinkClose is not null) line.Append(activeHyperlinkClose);
            line.Append(SgrReset);
            rows.Add(line.ToString());
        }
        return new(rows, rowStarts.ToArray(), text.Length);
    }

    private static List<WrapToken> Tokenize(string text)
    {
        var tokens = new List<WrapToken>();
        var pendingControls = new StringBuilder();
        WrapToken? current = null;

        void FlushCurrent()
        {
            if (current is null) return;
            tokens.Add(current);
            current = null;
        }

        void AddContent(WrapKind kind, string content, int width, int sourceStart, int sourceEnd)
        {
            if (kind is WrapKind.Cjk or WrapKind.Marker or WrapKind.Tab)
            {
                FlushCurrent();
                tokens.Add(new(kind, [new(pendingControls.ToString(), content, "", width, sourceStart, sourceEnd, kind)]));
                pendingControls.Clear();
                return;
            }
            if (current?.Kind != kind)
            {
                FlushCurrent();
                current = new(kind);
            }
            current.Atoms.Add(new(pendingControls.ToString(), content, "", width, sourceStart, sourceEnd, kind));
            pendingControls.Clear();
            current.Width += width;
        }

        for (var offset = 0; offset < text.Length;)
        {
            if (TerminalImageRenderer.TryReadMarker(text, offset, out var markerLength, out _, out _))
            {
                AddContent(WrapKind.Marker, text.Substring(offset, markerLength), 0, offset, offset + markerLength);
                offset += markerLength;
                continue;
            }
            if (TryReadEscape(text, offset, out var escapeLength, out _, out _))
            {
                pendingControls.Append(text, offset, escapeLength);
                offset += escapeLength;
                continue;
            }
            if (text[offset] is '\r' or '\n')
            {
                FlushCurrent();
                var newlineLength = text[offset] == '\r' && offset + 1 < text.Length && text[offset + 1] == '\n' ? 2 : 1;
                tokens.Add(new(WrapKind.Newline,
                    [new(pendingControls.ToString(), "", "", 0, offset, offset + newlineLength, WrapKind.Newline)]));
                pendingControls.Clear();
                offset += newlineLength;
                continue;
            }
            if (text[offset] == '\t')
            {
                AddContent(WrapKind.Tab, "\t", 0, offset, offset + 1);
                offset++;
                continue;
            }

            var element = StringInfo.GetNextTextElement(text, offset);
            var safe = Sanitize(element);
            var kind = safe.EnumerateRunes().All(Rune.IsWhiteSpace) ? WrapKind.Whitespace
                : IsCjkBreak(safe) ? WrapKind.Cjk
                : WrapKind.Word;
            AddContent(kind, safe, TerminalCells.Width(safe), offset, offset + element.Length);
            offset += element.Length;
        }

        FlushCurrent();
        if (pendingControls.Length > 0)
        {
            if (tokens.Count > 0)
                tokens[^1].Atoms[^1].Suffix = pendingControls.ToString();
            else
                tokens.Add(new(WrapKind.Control,
                    [new(pendingControls.ToString(), "", "", 0, text.Length, text.Length, WrapKind.Control)]));
        }
        return tokens;
    }

    private static bool IsCjkBreak(string element) => element.EnumerateRunes().Any(rune =>
        rune.Value is >= 0x2E80 and <= 0x2FFF or
            >= 0x3040 and <= 0x30FF or
            >= 0x3100 and <= 0x312F or
            >= 0x3130 and <= 0x318F or
            >= 0x31A0 and <= 0x31BF or
            >= 0x31F0 and <= 0x31FF or
            >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xA960 and <= 0xA97F or
            >= 0xAC00 and <= 0xD7FF or
            >= 0xF900 and <= 0xFAFF or
            >= 0xFF66 and <= 0xFF9D or
            >= 0x20000 and <= 0x323AF);

    public static string StripFormatting(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var output = new StringBuilder(text.Length);
        for (var offset = 0; offset < text.Length;)
        {
            if (TerminalImageRenderer.TryReadMarker(text, offset, out var markerLength, out _, out _))
            {
                offset += markerLength;
                continue;
            }
            if (TryReadEscape(text, offset, out var length, out _, out _))
            {
                offset += length;
                continue;
            }
            var element = StringInfo.GetNextTextElement(text, offset);
            output.Append(Sanitize(element));
            offset += element.Length;
        }
        return output.ToString();
    }

    public static (int Start, int End) CellRangeAt(string text, int cell)
    {
        ArgumentNullException.ThrowIfNull(text);
        cell = Math.Max(0, cell);
        var used = 0;
        for (var offset = 0; offset < text.Length;)
        {
            if (TerminalImageRenderer.TryReadMarker(text, offset, out var markerLength, out _, out _))
            {
                offset += markerLength;
                continue;
            }
            if (TryReadEscape(text, offset, out var length, out _, out _))
            {
                offset += length;
                continue;
            }
            if (text[offset] is '\r' or '\n') break;
            var element = StringInfo.GetNextTextElement(text, offset);
            var width = Math.Max(0, TerminalCells.Width(Sanitize(element)));
            if (width > 0 && cell < used + width) return (used, used + width);
            used += width;
            offset += element.Length;
        }
        return (used, used);
    }

    public static string SliceCells(string text, int startCell, int endCell)
    {
        ArgumentNullException.ThrowIfNull(text);
        startCell = Math.Max(0, startCell);
        endCell = Math.Max(startCell, endCell);
        var output = new StringBuilder(text.Length);
        var used = 0;
        for (var offset = 0; offset < text.Length;)
        {
            if (TerminalImageRenderer.TryReadMarker(text, offset, out var markerLength, out _, out _))
            {
                offset += markerLength;
                continue;
            }
            if (TryReadEscape(text, offset, out var length, out _, out _))
            {
                offset += length;
                continue;
            }
            if (text[offset] is '\r' or '\n') break;
            var element = StringInfo.GetNextTextElement(text, offset);
            var safe = Sanitize(element);
            var width = Math.Max(0, TerminalCells.Width(safe));
            if (width > 0 && used < endCell && used + width > startCell) output.Append(safe);
            used += width;
            offset += element.Length;
        }
        return output.ToString();
    }

    public static string HighlightCells(string text, int startCell, int endCell)
    {
        ArgumentNullException.ThrowIfNull(text);
        startCell = Math.Max(0, startCell);
        endCell = Math.Max(startCell, endCell);
        if (startCell == endCell) return text;

        var output = new StringBuilder(text.Length + 16);
        var used = 0;
        var highlighting = false;
        for (var offset = 0; offset < text.Length;)
        {
            if (TerminalImageRenderer.TryReadMarker(text, offset, out var markerLength, out _, out _))
            {
                output.Append(text, offset, markerLength);
                offset += markerLength;
                continue;
            }
            if (TryReadEscape(text, offset, out var length, out var kind, out var payload))
            {
                var sequence = text.Substring(offset, length);
                output.Append(sequence);
                if (highlighting && kind == EscapeKind.Sgr &&
                    (ResetsSgr(payload) || DisablesInverse(payload)))
                    output.Append(SelectionOn);
                offset += length;
                continue;
            }
            if (text[offset] is '\r' or '\n')
            {
                output.Append(text[offset++]);
                continue;
            }
            var element = StringInfo.GetNextTextElement(text, offset);
            var safe = Sanitize(element);
            var width = Math.Max(0, TerminalCells.Width(safe));
            var isSelected = width > 0 && used < endCell && used + width > startCell;
            if (isSelected && !highlighting)
            {
                output.Append(SelectionOn);
                highlighting = true;
            }
            output.Append(safe);
            used += width;
            if (highlighting && used >= endCell)
            {
                output.Append(SelectionOff);
                highlighting = false;
            }
            offset += element.Length;
        }
        if (highlighting) output.Append(SelectionOff);
        return output.ToString();
    }

    public static string Sanitize(string element)
    {
        var output = new StringBuilder(element.Length);
        foreach (var rune in element.EnumerateRunes())
            output.Append(Rune.IsControl(rune) ? " " : rune.ToString());
        return output.ToString();
    }

    internal static bool TryReadEscape(string text, int offset, out int length) =>
        TryReadEscape(text, offset, out length, out _, out _);

    private static bool TryReadEscape(string text, int offset, out int length, out EscapeKind kind, out string payload)
    {
        length = 0;
        kind = EscapeKind.Other;
        payload = "";
        if (offset + 1 >= text.Length || text[offset] != '\u001b') return false;

        if (text[offset + 1] == '[')
        {
            var end = offset + 2;
            while (end < text.Length && text[end] is >= '\u0020' and <= '\u003f') end++;
            if (end >= text.Length || text[end] is < '\u0040' or > '\u007e') return false;
            length = end - offset + 1;
            payload = text[(offset + 2)..end];
            if (text[end] == 'm' && payload.All(character => char.IsAsciiDigit(character) || character is ';' or ':'))
                kind = EscapeKind.Sgr;
            return true;
        }

        if (text[offset + 1] == ']')
        {
            var end = offset + 2;
            while (end < text.Length && text[end] != '\a' &&
                !(text[end] == '\u001b' && end + 1 < text.Length && text[end + 1] == '\\'))
                end++;
            if (end >= text.Length) return false;
            var terminatorLength = text[end] == '\a' ? 1 : 2;
            length = end - offset + terminatorLength;
            payload = text[(offset + 2)..end];
            if (payload.StartsWith("8;", StringComparison.Ordinal)) kind = EscapeKind.Hyperlink;
            return true;
        }

        if (text[offset + 1] is >= '\u0040' and <= '\u005f')
        {
            length = 2;
            return true;
        }
        return false;
    }

    private static bool ResetsSgr(string payload)
    {
        foreach (var parameter in payload.Split(';'))
            if (parameter.Length == 0 || parameter == "0") return true;
        return false;
    }

    private static bool DisablesInverse(string payload) => payload.Split(';').Contains("27", StringComparer.Ordinal);

    private static string HyperlinkTarget(string payload)
    {
        var parameterSeparator = payload.IndexOf(';');
        if (parameterSeparator < 0) return "";
        var targetSeparator = payload.IndexOf(';', parameterSeparator + 1);
        return targetSeparator < 0 ? "" : payload[(targetSeparator + 1)..];
    }

    private enum WrapKind { Word, Whitespace, Tab, Newline, Cjk, Marker, Control }

    private sealed class WrapToken
    {
        public WrapToken(WrapKind kind)
        {
            Kind = kind;
            Atoms = [];
        }

        public WrapToken(WrapKind kind, List<WrapAtom> atoms)
        {
            Kind = kind;
            Atoms = atoms;
            Width = atoms.Sum(atom => atom.Width);
        }

        public WrapKind Kind { get; }
        public List<WrapAtom> Atoms { get; }
        public int Width { get; set; }
        public int SourceStart => Atoms.Count == 0 ? 0 : Atoms[0].SourceStart;
    }

    private sealed class WrapAtom(string prefix, string content, string suffix, int width, int sourceStart, int sourceEnd, WrapKind kind)
    {
        public string Prefix { get; } = prefix;
        public string Content { get; } = content;
        public string Suffix { get; set; } = suffix;
        public int Width { get; } = width;
        public int SourceStart { get; } = sourceStart;
        public int SourceEnd { get; } = sourceEnd;
        public WrapKind Kind { get; } = kind;
    }

    private enum EscapeKind { Other, Sgr, Hyperlink }
}
