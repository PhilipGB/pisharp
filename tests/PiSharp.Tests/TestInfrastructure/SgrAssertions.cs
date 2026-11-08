using System.Text.RegularExpressions;

namespace PiSharp.Tests;

internal static class SgrAssertions
{
    internal static void AssertStyleAt(string text, string target, bool? bold = null, bool? italic = null,
        bool? underline = null, bool? foreground = null, bool? dim = null, int occurrence = 0)
    {
        var offset = 0;
        for (var index = 0; index <= occurrence; index++)
        {
            offset = text.IndexOf(target, offset, StringComparison.Ordinal);
            Assert.NotEqual(-1, offset);
            if (index < occurrence) offset += target.Length;
        }

        var state = default(SgrState);
        for (var index = 0; index < offset;)
        {
            if (index + 1 < text.Length && text[index] == '\u001b' && text[index + 1] == '[')
            {
                var end = index + 2;
                while (end < text.Length && !(text[end] >= '@' && text[end] <= '~')) end++;
                if (end >= offset) break;
                if (text[end] != 'm')
                {
                    index = end + 1;
                    continue;
                }
                var parameters = text[(index + 2)..end].Split(';');
                for (var parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                {
                    var parameter = parameters[parameterIndex];
                    var value = parameter.Length == 0 ? 0 : int.Parse(parameter);
                    if (value is 38 or 48 or 58)
                    {
                        if (parameterIndex + 1 < parameters.Length)
                        {
                            var colorMode = int.Parse(parameters[++parameterIndex]);
                            if (colorMode == 2) parameterIndex = Math.Min(parameters.Length, parameterIndex + 3);
                            else if (colorMode == 5) parameterIndex = Math.Min(parameters.Length, parameterIndex + 1);
                        }
                        if (value == 38) state = state with { Foreground = true };
                        continue;
                    }
                    state = value switch
                    {
                        0 => default,
                        1 => state with { Bold = true },
                        2 => state with { Dim = true },
                        3 => state with { Italic = true },
                        4 => state with { Underline = true },
                        9 => state with { Strike = true },
                        22 => state with { Bold = false, Dim = false },
                        23 => state with { Italic = false },
                        24 => state with { Underline = false },
                        29 => state with { Strike = false },
                        39 => state with { Foreground = false },
                        >= 30 and <= 37 or >= 90 and <= 97 => state with { Foreground = true },
                        _ => state
                    };
                }
                index = end + 1;
                continue;
            }
            index++;
        }

        if (bold is { } expectedBold) Assert.Equal(expectedBold, state.Bold);
        if (italic is { } expectedItalic) Assert.Equal(expectedItalic, state.Italic);
        if (underline is { } expectedUnderline) Assert.Equal(expectedUnderline, state.Underline);
        if (dim is { } expectedDim) Assert.Equal(expectedDim, state.Dim);
        if (foreground is { } expectedForeground)
            Assert.True(expectedForeground == state.Foreground,
                $"Foreground state before '{target}': {string.Join(' ', Regex.Matches(text[..offset], "\\u001b\\[[0-9;]*m").Select(match => match.Value))}");
    }

    internal static string StripSgr(string text) => Regex.Replace(text, "\\u001b\\[[0-9;]*m", "");

    private readonly record struct SgrState(bool Bold, bool Dim, bool Italic, bool Underline, bool Strike, bool Foreground);
}
