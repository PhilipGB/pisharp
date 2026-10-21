using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Tui;

/// <summary>Applies Pi's theme scopes to a practical set of code fence languages.</summary>
internal static partial class TerminalSyntaxHighlighter
{
    private static readonly HashSet<string> s_supportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "csharp", "cs", "c#", "javascript", "js", "typescript", "ts", "python", "py", "json", "bash", "sh", "shell"
    };
    private static readonly HashSet<string> s_plaintextLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "plaintext", "plain", "txt"
    };

    private static readonly HashSet<string> s_keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "base", "break", "case", "catch", "class", "const", "continue",
        "def", "default", "delegate", "do", "elif", "else", "enum", "except", "extends", "False", "false",
        "finally", "for", "foreach", "from", "function", "if", "implements", "import", "in", "interface",
        "is", "lambda", "let", "match", "namespace", "new", "None", "none", "null", "of", "package",
        "pass", "private", "protected", "public", "raise", "record", "return", "sealed", "self", "static",
        "struct", "super", "switch", "this", "throw", "True", "true", "try", "type", "typeof", "var", "void",
        "while", "with", "yield", "fi", "then", "elif", "done", "esac", "export", "local", "readonly", "select",
        "until"
    };

    private static readonly HashSet<string> s_csharpBuiltIns = new(StringComparer.Ordinal)
    {
        "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object", "short", "string", "uint",
        "ulong", "ushort"
    };

    private static readonly HashSet<string> s_shellBuiltIns = new(StringComparer.Ordinal)
    {
        "echo", "printf", "cd", "pwd", "read", "test", "source", "unset", "set", "shift", "trap"
    };

    private static readonly Regex s_tokenPattern = TokenPattern();

    public static bool Supports(string? language) => s_supportedLanguages.Contains(NormalizeLanguage(language));

    public static string Highlight(string code, string? language, TerminalTheme theme)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(theme);
        var normalized = NormalizeLanguage(language);
        if (s_plaintextLanguages.Contains(normalized)) return code;
        if (!s_supportedLanguages.Contains(normalized)) return theme.Style("mdCodeBlock", code);

        var output = new StringBuilder(code.Length + 64);
        var position = 0;
        var expectDeclarationName = false;
        var expectPythonFunctionName = false;
        var expectPythonFunctionOpen = false;
        var inPythonParameters = false;
        var pythonParametersClosed = false;
        foreach (Match match in s_tokenPattern.Matches(code))
        {
            var gap = code[position..match.Index];
            AppendGap(output, gap, expectPythonFunctionName, theme);

            var text = match.Value;
            string? tokenStyle = null;
            if (match.Groups["comment"].Success)
            {
                tokenStyle = "syntaxComment";
            }
            else if (match.Groups["string"].Success)
            {
                tokenStyle = normalized == "json" && IsJsonPropertyName(code, match.Index + match.Length)
                    ? "syntaxVariable"
                    : "syntaxString";
            }
            else if (match.Groups["number"].Success)
            {
                if (normalized != "bash") tokenStyle = "syntaxNumber";
            }
            else if (match.Groups["identifier"].Success)
            {
                var identifier = text;
                if (normalized == "python" && expectPythonFunctionName)
                {
                    tokenStyle = "syntaxFunction";
                    expectPythonFunctionName = false;
                    expectPythonFunctionOpen = true;
                }
                else if (normalized == "python" && inPythonParameters)
                {
                    tokenStyle = "syntaxVariable";
                }
                else if (expectDeclarationName)
                {
                    tokenStyle = "syntaxFunction";
                    expectDeclarationName = false;
                }
                else if (normalized == "python" && (identifier is "True" or "False" or "None"))
                {
                    tokenStyle = "syntaxNumber";
                }
                else if (normalized == "json")
                {
                    if (identifier is "true" or "false" or "null") tokenStyle = "syntaxNumber";
                }
                else if ((normalized == "javascript" || normalized == "typescript") && identifier == "console")
                {
                    tokenStyle = "syntaxType";
                }
                else if ((normalized == "javascript" || normalized == "typescript") &&
                    identifier is "true" or "false" or "null")
                {
                    tokenStyle = "syntaxNumber";
                }
                else if (normalized == "bash" && s_shellBuiltIns.Contains(identifier))
                {
                    tokenStyle = "syntaxType";
                }
                else if (s_keywords.Contains(identifier))
                {
                    tokenStyle = "syntaxKeyword";
                    if (identifier is "class" or "struct" or "interface" or "enum" or "record")
                        expectDeclarationName = true;
                    if (normalized == "python" && identifier == "def") expectPythonFunctionName = true;
                }
                else if (normalized == "csharp" && s_csharpBuiltIns.Contains(identifier))
                {
                    tokenStyle = "syntaxType";
                }
            }
            else if (normalized == "python" && expectPythonFunctionOpen && text == "(")
            {
                tokenStyle = "syntaxFunction";
                expectPythonFunctionOpen = false;
                inPythonParameters = true;
            }
            else if (normalized == "python" && inPythonParameters && text == ")")
            {
                tokenStyle = "syntaxFunction";
                inPythonParameters = false;
                pythonParametersClosed = true;
            }
            else if (normalized == "python" && pythonParametersClosed && text == ":")
            {
                tokenStyle = "syntaxFunction";
                pythonParametersClosed = false;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (normalized == "python" && expectPythonFunctionOpen && text.Length > 0 && text.All(char.IsWhiteSpace))
                    {
                        tokenStyle = "syntaxFunction";
                    }
                    else if (text is not (" " or "\t" or "\r" or "\n"))
                    {
                        if (expectPythonFunctionName && text.Length == 0)
                            tokenStyle = "syntaxFunction";
                        if (expectDeclarationName)
                            tokenStyle = null;
                    }
                }
            }

            if (tokenStyle is null) output.Append(text);
            else output.Append(theme.Fg(tokenStyle)).Append(text).Append("\u001b[39m");

            position = match.Index + match.Length;
        }
        if (position < code.Length) AppendGap(output, code[position..], expectPythonFunctionName, theme);
        return output.ToString();
    }

    private static void AppendGap(StringBuilder output, string gap, bool functionPrefix, TerminalTheme theme)
    {
        if (functionPrefix && gap.Length > 0)
            output.Append(theme.Fg("syntaxFunction")).Append(gap).Append("\u001b[39m");
        else
            output.Append(gap);
    }

    private static bool IsJsonPropertyName(string source, int next)
    {
        while (next < source.Length && char.IsWhiteSpace(source[next])) next++;
        return next < source.Length && source[next] == ':';
    }

    private static string NormalizeLanguage(string? language) => (language ?? "").Trim().ToLowerInvariant() switch
    {
        "c#" or "cs" => "csharp",
        "js" => "javascript",
        "ts" => "typescript",
        "py" => "python",
        "sh" or "shell" => "bash",
        var value => value
    };

    [GeneratedRegex("(?<comment>//[^\\r\\n]*|#[^\\r\\n]*)|(?<string>@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*')|(?<number>\\b(?:0[xX][0-9a-fA-F]+|\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)\\b)|(?<identifier>[$@]?[_\\p{L}][$_\\p{L}\\p{N}]*)|(?<operator>=>|==|!=|<=|>=|\\+\\+|--|&&|\\|\\||\\?\\?|\\+|-|\\*|/|%|=|<|>|!|\\||&)|(?<punctuation>[{}()\\[\\].,;:])", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex TokenPattern();
}
