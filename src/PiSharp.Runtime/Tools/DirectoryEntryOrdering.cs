using System.Globalization;
using System.Text;

namespace PiSharp.Runtime.Tools;

internal static class DirectoryEntryOrdering
{
    public static IEnumerable<string> Sort(IEnumerable<string> names) => names
        // libuv's readdir byte order breaks ties in the stable locale sort.
        .Order(Comparer<string>.Create(CompareScalars))
        .Select(name => (Name: name, Key: Lower(name)))
        .OrderBy(entry => entry.Key, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: false))
        .Select(entry => entry.Name);

    private static int CompareScalars(string? left, string? right)
    {
        var first = left.AsSpan().EnumerateRunes();
        var second = right.AsSpan().EnumerateRunes();
        while (first.MoveNext())
        {
            if (!second.MoveNext()) return 1;
            var compared = first.Current.Value.CompareTo(second.Current.Value);
            if (compared != 0) return compared;
        }
        return second.MoveNext() ? -1 : 0;
    }

    private static string Lower(string value)
    {
        // JavaScript default casing expands dotted I and applies contextual final sigma.
        if (!value.Contains('\u0130') && !value.Contains('\u03a3')) return value.ToLowerInvariant();
        var runes = value.EnumerateRunes().ToArray();
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < runes.Length; index++)
        {
            if (runes[index].Value == 0x130) result.Append("i\u0307");
            else if (runes[index].Value == 0x3a3)
                result.Append(HasCased(runes, index, -1) && !HasCased(runes, index, 1) ? '\u03c2' : '\u03c3');
            else result.Append(Rune.ToLowerInvariant(runes[index]));
        }
        return result.ToString();
    }

    private static bool HasCased(Rune[] runes, int index, int direction)
    {
        for (index += direction; index >= 0 && index < runes.Length; index += direction)
        {
            var rune = runes[index];
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format
                or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol ||
                rune.Value is 0x27 or 0x2e or 0x3a or 0xb7 or 0x387 or 0x55f or 0x5f4 or 0x2018 or 0x2019
                    or 0x2024 or 0x2027 or 0xfe13 or 0xfe52 or 0xfe55 or 0xff07 or 0xff0e or 0xff1a) continue;
            return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter ||
                rune.Value is 0xaa or 0xba or >= 0x2160 and <= 0x217f or >= 0x24b6 and <= 0x24e9
                    or >= 0x1f130 and <= 0x1f149 or >= 0x1f150 and <= 0x1f169 or >= 0x1f170 and <= 0x1f189;
        }
        return false;
    }
}
