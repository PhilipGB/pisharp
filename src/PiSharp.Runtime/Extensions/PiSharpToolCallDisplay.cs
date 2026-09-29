using System.Text;
using System.Text.Json;

namespace PiSharp.Runtime.Extensions;

/// <summary>Bounded call-header formatting for tools without a specialized renderer.</summary>
public static class PiSharpToolCallDisplay
{
    private const int CollapsedArgumentCharacters = 100;

    public static PiSharpToolRenderView Format(string title, IReadOnlyDictionary<string, object?> arguments,
        bool expanded)
    {
        var spans = new List<PiSharpToolTextSpan> { new(title, PiSharpToolTextStyle.Title, Bold: true) };
        if (arguments.Count == 0) return new(spans);
        if (expanded)
        {
            var lines = new StringBuilder();
            foreach (var (key, value) in arguments)
            {
                var content = value switch
                {
                    string literal => literal,
                    JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
                    _ => JsonSerializer.Serialize(value)
                };
                lines.Append("\n  ").Append(key).Append(": ")
                    .Append(content.Replace("\t", "    ", StringComparison.Ordinal)
                        .Replace("\r", "", StringComparison.Ordinal)
                        .Replace("\n", "\n    ", StringComparison.Ordinal));
            }
            spans.Add(new(lines.ToString(), PiSharpToolTextStyle.Muted));
            return new(spans);
        }

        var pairs = string.Join(" ", arguments.Select(pair =>
            pair.Key + "=" + JsonSerializer.Serialize(pair.Value)));
        if (pairs.Length > CollapsedArgumentCharacters)
            pairs = pairs[..(CollapsedArgumentCharacters - 3)] + "...";
        spans.Add(new(" " + pairs, PiSharpToolTextStyle.Muted));
        return new(spans);
    }
}
