using System.Text.Json;

namespace PiSharp.Runtime.Codemode;

internal sealed record CodemodeSource(string Code, long MaxOutputTokens = 10000, int TimeoutMilliseconds = 30000)
{
    public static CodemodeSource Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Expected nonempty JavaScript source.");
        var newline = source.IndexOf('\n');
        var first = (newline < 0 ? source : source[..newline]).TrimStart().TrimEnd('\r');
        const string prefix = "// @options:";
        if (!first.StartsWith(prefix, StringComparison.Ordinal)) return new(source);
        var code = newline < 0 ? "" : source[newline..];
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("The @options line must be followed by JavaScript source.");
        using var document = JsonDocument.Parse(first[prefix.Length..]);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("@options must be a JSON object.");
        long tokens = 10000;
        var timeout = 30000;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name is not ("max_output_tokens" or "timeout_ms"))
                throw new ArgumentException($"@options only supports max_output_tokens and timeout_ms; got {property.Name}.");
            if (!property.Value.TryGetInt64(out var value) || value < 0 || value > 9007199254740991)
                throw new ArgumentException($"@options field {property.Name} must be a non-negative safe integer.");
            if (property.Name == "max_output_tokens") tokens = value;
            else
            {
                if (value is 0 or > int.MaxValue) throw new ArgumentException("@options timeout_ms must be a positive integer up to 2147483647.");
                timeout = (int)Math.Min(value, 30000);
            }
        }
        return new(code, tokens, timeout);
    }
}
