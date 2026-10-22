using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Renders the common, terminal-friendly subset of Pi's LaTeX presentation.</summary>
internal static class TerminalLatexRenderer
{
    internal const int MaximumNestingDepth = 64;

    private static readonly IReadOnlyDictionary<string, string> Symbols = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["alpha"] = "α",
        ["beta"] = "β",
        ["gamma"] = "γ",
        ["delta"] = "δ",
        ["epsilon"] = "ϵ",
        ["varepsilon"] = "ε",
        ["theta"] = "θ",
        ["lambda"] = "λ",
        ["mu"] = "μ",
        ["pi"] = "π",
        ["rho"] = "ρ",
        ["sigma"] = "σ",
        ["tau"] = "τ",
        ["phi"] = "ϕ",
        ["varphi"] = "φ",
        ["omega"] = "ω",
        ["Gamma"] = "Γ",
        ["Delta"] = "Δ",
        ["Theta"] = "Θ",
        ["Lambda"] = "Λ",
        ["Pi"] = "Π",
        ["Sigma"] = "Σ",
        ["Phi"] = "Φ",
        ["Omega"] = "Ω",
        ["pm"] = "±",
        ["mp"] = "∓",
        ["times"] = "×",
        ["div"] = "÷",
        ["cdot"] = "·",
        ["otimes"] = "⊗",
        ["oplus"] = "⊕",
        ["in"] = "∈",
        ["notin"] = "∉",
        ["subset"] = "⊂",
        ["subseteq"] = "⊆",
        ["supset"] = "⊃",
        ["supseteq"] = "⊇",
        ["cap"] = "∩",
        ["cup"] = "∪",
        ["le"] = "≤",
        ["leq"] = "≤",
        ["ge"] = "≥",
        ["geq"] = "≥",
        ["ne"] = "≠",
        ["neq"] = "≠",
        ["approx"] = "≈",
        ["equiv"] = "≡",
        ["sim"] = "∼",
        ["propto"] = "∝",
        ["forall"] = "∀",
        ["exists"] = "∃",
        ["land"] = "∧",
        ["lor"] = "∨",
        ["neg"] = "¬",
        ["to"] = "→",
        ["rightarrow"] = "→",
        ["leftarrow"] = "←",
        ["leftrightarrow"] = "↔",
        ["Rightarrow"] = "⇒",
        ["Leftarrow"] = "⇐",
        ["Leftrightarrow"] = "⇔",
        ["mapsto"] = "↦",
        ["partial"] = "∂",
        ["nabla"] = "∇",
        ["int"] = "∫",
        ["iint"] = "∬",
        ["oint"] = "∮",
        ["sum"] = "∑",
        ["prod"] = "∏",
        ["infty"] = "∞",
        ["emptyset"] = "∅",
        ["angle"] = "∠",
        ["therefore"] = "∴",
        ["ldots"] = "…",
        ["cdots"] = "⋯",
        ["vdots"] = "⋮",
        ["ell"] = "ℓ",
        ["hbar"] = "ℏ",
        ["langle"] = "⟨",
        ["rangle"] = "⟩",
        ["vert"] = "|",
        ["Vert"] = "‖",
        ["lfloor"] = "⌊",
        ["rfloor"] = "⌋",
        ["lceil"] = "⌈",
        ["rceil"] = "⌉",
        ["{"] = "{",
        ["}"] = "}",
        ["_"] = "_",
        ["%"] = "%",
        ["#"] = "#",
        ["$"] = "$",
        ["&"] = "&",
        ["zeta"] = "ζ",
        ["eta"] = "η",
        ["vartheta"] = "ϑ",
        ["iota"] = "ι",
        ["kappa"] = "κ",
        ["varkappa"] = "ϰ",
        ["nu"] = "ν",
        ["xi"] = "ξ",
        ["varpi"] = "ϖ",
        ["varrho"] = "ϱ",
        ["varsigma"] = "ς",
        ["upsilon"] = "υ",
        ["chi"] = "χ",
        ["psi"] = "ψ",
        ["Xi"] = "Ξ",
        ["Upsilon"] = "Υ",
        ["Psi"] = "Ψ",
        ["ast"] = "∗",
        ["star"] = "⋆",
        ["circ"] = "∘",
        ["bullet"] = "•",
        ["ominus"] = "⊖",
        ["oslash"] = "⊘",
        ["odot"] = "⊙",
        ["bigcirc"] = "○",
        ["dagger"] = "†",
        ["ddagger"] = "‡",
        ["amalg"] = "⨿",
        ["uplus"] = "⊎",
        ["sqcap"] = "⊓",
        ["sqcup"] = "⊔",
        ["bowtie"] = "⋈",
        ["Join"] = "⋈",
        ["ltimes"] = "⋉",
        ["rtimes"] = "⋊",
        ["leftouterjoin"] = "⟕",
        ["rightouterjoin"] = "⟖",
        ["fullouterjoin"] = "⟗",
        ["triangleleft"] = "◁",
        ["triangleright"] = "▷",
        ["wr"] = "≀",
        ["bigcap"] = "⋂",
        ["bigcup"] = "⋃",
        ["bigwedge"] = "⋀",
        ["bigvee"] = "⋁",
        ["bigsqcup"] = "⨆",
        ["biguplus"] = "⨄",
        ["bigoplus"] = "⨁",
        ["bigotimes"] = "⨂",
        ["bigodot"] = "⨀",
        ["setminus"] = "∖",
        ["ni"] = "∋",
        ["sqsubset"] = "⊏",
        ["sqsupset"] = "⊐",
        ["sqsubseteq"] = "⊑",
        ["sqsupseteq"] = "⊒",
        ["prec"] = "≺",
        ["preceq"] = "≼",
        ["succ"] = "≻",
        ["succeq"] = "≽",
        ["ll"] = "≪",
        ["gg"] = "≫",
        ["leqslant"] = "≤",
        ["geqslant"] = "≥",
        ["simeq"] = "≃",
        ["cong"] = "≅",
        ["asymp"] = "≍",
        ["doteq"] = "≐",
        ["parallel"] = "∥",
        ["perp"] = "⊥",
        ["mid"] = "∣",
        ["vdash"] = "⊢",
        ["dashv"] = "⊣",
        ["models"] = "⊨",
        ["Vdash"] = "⊩",
        ["Vvdash"] = "⊪",
        ["nvdash"] = "⊬",
        ["nvDash"] = "⊭",
        ["nexists"] = "∄",
        ["wedge"] = "∧",
        ["vee"] = "∨",
        ["longrightarrow"] = "→",
        ["longleftarrow"] = "←",
        ["gets"] = "←",
        ["longleftrightarrow"] = "↔",
        ["hookleftarrow"] = "↩",
        ["hookrightarrow"] = "↪",
        ["twoheadleftarrow"] = "↞",
        ["twoheadrightarrow"] = "↠",
        ["leftharpoonup"] = "↼",
        ["leftharpoondown"] = "↽",
        ["rightharpoonup"] = "⇀",
        ["rightharpoondown"] = "⇁",
        ["rightleftharpoons"] = "⇌",
        ["leftrightharpoons"] = "⇋",
        ["nearrow"] = "↗",
        ["searrow"] = "↘",
        ["swarrow"] = "↙",
        ["nwarrow"] = "↖",
        ["rightsquigarrow"] = "⇝",
        ["leadsto"] = "⇝",
        ["Longrightarrow"] = "⇒",
        ["Longleftarrow"] = "⇐",
        ["Longleftrightarrow"] = "⇔",
        ["implies"] = "⇒",
        ["iff"] = "⇔",
        ["longmapsto"] = "↦",
        ["uparrow"] = "↑",
        ["downarrow"] = "↓",
        ["iiint"] = "∭",
        ["coprod"] = "∐",
        ["varnothing"] = "∅",
        ["because"] = "∵",
        ["aleph"] = "ℵ",
        ["beth"] = "ℶ",
        ["gimel"] = "ℷ",
        ["daleth"] = "ℸ",
        ["top"] = "⊤",
        ["bot"] = "⊥",
        ["triangle"] = "△",
        ["square"] = "□",
        ["lozenge"] = "◊",
        ["checkmark"] = "✓",
        ["complement"] = "∁",
        ["wp"] = "℘",
        ["prime"] = "′",
        ["dots"] = "…",
        ["ddots"] = "⋱",
        ["Im"] = "ℑ",
        ["Re"] = "ℜ",
        ["lvert"] = "|",
        ["rvert"] = "|",
        ["lVert"] = "‖",
        ["rVert"] = "‖",
        ["lbrace"] = "{",
        ["rbrace"] = "}",
        ["backslash"] = "\\",
        ["colon"] = ":",
    };


    private static readonly HashSet<string> SpacedSymbols = new(StringComparer.Ordinal)
    {
        "cdot", "times",
        "Leftarrow",
        "Leftrightarrow",
        "Longleftarrow",
        "Longleftrightarrow",
        "Longrightarrow",
        "Rightarrow",
        "Join",
        "Vdash",
        "Vvdash",
        "approx",
        "asymp",
        "bowtie",
        "cong",
        "dashv",
        "fullouterjoin",
        "doteq",
        "downarrow",
        "equiv",
        "ge",
        "geq",
        "geqslant",
        "gets",
        "gg",
        "hookleftarrow",
        "hookrightarrow",
        "iff",
        "implies",
        "in",
        "leadsto",
        "le",
        "leftarrow",
        "leftharpoondown",
        "leftharpoonup",
        "leftrightarrow",
        "leftrightharpoons",
        "leftouterjoin",
        "leq",
        "leqslant",
        "ll",
        "longleftarrow",
        "longleftrightarrow",
        "longmapsto",
        "longrightarrow",
        "ltimes",
        "mapsto",
        "mid",
        "models",
        "ne",
        "nearrow",
        "neq",
        "ni",
        "notin",
        "nvdash",
        "nvDash",
        "nwarrow",
        "parallel",
        "perp",
        "prec",
        "preceq",
        "propto",
        "rightharpoondown",
        "rightharpoonup",
        "rightleftharpoons",
        "rightouterjoin",
        "rightarrow",
        "rightsquigarrow",
        "rtimes",
        "searrow",
        "sim",
        "simeq",
        "sqsubset",
        "sqsubseteq",
        "sqsupset",
        "sqsupseteq",
        "subset",
        "subseteq",
        "succ",
        "succeq",
        "supset",
        "supseteq",
        "swarrow",
        "to",
        "triangleleft",
        "triangleright",
        "twoheadleftarrow",
        "twoheadrightarrow",
        "uparrow",
        "vdash",
    };

    private static readonly IReadOnlyDictionary<char, string> Superscripts = new Dictionary<char, string>
    {
        ['0'] = "⁰",
        ['1'] = "¹",
        ['2'] = "²",
        ['3'] = "³",
        ['4'] = "⁴",
        ['5'] = "⁵",
        ['6'] = "⁶",
        ['7'] = "⁷",
        ['8'] = "⁸",
        ['9'] = "⁹",
        ['+'] = "⁺",
        ['-'] = "⁻",
        ['='] = "⁼",
        ['('] = "⁽",
        [')'] = "⁾",
        ['a'] = "ᵃ",
        ['b'] = "ᵇ",
        ['c'] = "ᶜ",
        ['d'] = "ᵈ",
        ['e'] = "ᵉ",
        ['f'] = "ᶠ",
        ['g'] = "ᵍ",
        ['h'] = "ʰ",
        ['i'] = "ⁱ",
        ['j'] = "ʲ",
        ['k'] = "ᵏ",
        ['l'] = "ˡ",
        ['m'] = "ᵐ",
        ['n'] = "ⁿ",
        ['o'] = "ᵒ",
        ['p'] = "ᵖ",
        ['r'] = "ʳ",
        ['s'] = "ˢ",
        ['t'] = "ᵗ",
        ['u'] = "ᵘ",
        ['v'] = "ᵛ",
        ['w'] = "ʷ",
        ['x'] = "ˣ",
        ['y'] = "ʸ",
        ['z'] = "ᶻ",
    };

    private static readonly IReadOnlyDictionary<char, string> Subscripts = new Dictionary<char, string>
    {
        ['0'] = "₀",
        ['1'] = "₁",
        ['2'] = "₂",
        ['3'] = "₃",
        ['4'] = "₄",
        ['5'] = "₅",
        ['6'] = "₆",
        ['7'] = "₇",
        ['8'] = "₈",
        ['9'] = "₉",
        ['+'] = "₊",
        ['-'] = "₋",
        ['='] = "₌",
        ['('] = "₍",
        [')'] = "₎",
        ['a'] = "ₐ",
        ['e'] = "ₑ",
        ['h'] = "ₕ",
        ['i'] = "ᵢ",
        ['j'] = "ⱼ",
        ['k'] = "ₖ",
        ['l'] = "ₗ",
        ['m'] = "ₘ",
        ['n'] = "ₙ",
        ['o'] = "ₒ",
        ['p'] = "ₚ",
        ['r'] = "ᵣ",
        ['s'] = "ₛ",
        ['t'] = "ₜ",
        ['u'] = "ᵤ",
        ['v'] = "ᵥ",
        ['x'] = "ₓ",
    };

    public static string? Render(string source, bool display = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = new Parser(source, display);
        var result = parser.Parse();
        return parser.Supported ? Normalize(result) : null;
    }

    private static string Normalize(string value)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim())
            .ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    private sealed class Parser(string source, bool display, int depth = 0)
    {
        private int _position;
        private int _depth = depth;
        private bool _stackFractions = true;

        public bool Supported { get; private set; } = true;

        public string Parse() => ParseSequence();

        private string ParseSequence(bool group = false)
        {
            if (!EnterNesting()) return "";
            try
            {
                return ParseSequenceCore(group);
            }
            finally
            {
                _depth--;
            }
        }

        private string ParseSequenceCore(bool group)
        {
            var result = new StringBuilder();
            while (Supported && _position < source.Length)
            {
                var current = source[_position];
                if (current == '}')
                {
                    if (!group) Supported = false;
                    else _position++;
                    return result.ToString();
                }
                if (current == '{')
                {
                    _position++;
                    result.Append(ParseSequence(group: true));
                    continue;
                }
                if (current == '\\')
                {
                    _position++;
                    result.Append(ParseCommand());
                    continue;
                }
                if (current is '^' or '_')
                {
                    AppendScripts(result, current);
                    continue;
                }
                if (char.IsWhiteSpace(current))
                {
                    while (_position < source.Length && char.IsWhiteSpace(source[_position])) _position++;
                    if (result.Length > 0 && result[^1] is not (' ' or '\n')) result.Append(' ');
                    continue;
                }
                _position++;
                if (current is '=' or '<' or '>')
                {
                    TrimEnd(result);
                    result.Append(' ').Append(current).Append(' ');
                }
                else if (current == '&')
                {
                    // Alignment markers do not contribute visible text.
                }
                else if (current == '~') result.Append(' ');
                else result.Append(current);
            }

            if (group) Supported = false;
            return result.ToString();
        }

        private string ParseCommand()
        {
            if (_position >= source.Length)
            {
                Supported = false;
                return "";
            }
            if (source[_position] is '\r' or '\n')
            {
                if (source[_position++] == '\r' && _position < source.Length && source[_position] == '\n') _position++;
                return " ";
            }

            var start = _position;
            if (char.IsAsciiLetter(source[_position]))
            {
                while (_position < source.Length && char.IsAsciiLetter(source[_position])) _position++;
            }
            else _position++;
            var command = source[start.._position];

            if (command is "," or ";" or ":" or "!" or " " or ">" or "quad" or "qquad" or "thinspace" or "enspace")
                return command == "!" ? "" : " ";
            if (command is "displaystyle" or "textstyle" or "scriptstyle" or "limits" or "nolimits" or "left" or "right" or "middle")
            {
                if (command is "left" or "right" or "middle" && _position < source.Length && source[_position] == '.') _position++;
                return "";
            }
            if (command is "\\") return "\n";
            if (command == "not") return AppendNot();
            if (command is "frac" or "dfrac" or "tfrac")
            {
                var shouldStack = display && _stackFractions && command != "tfrac";
                var numerator = ParseArgument(allowStackedFraction: !shouldStack);
                var denominator = ParseArgument(allowStackedFraction: !shouldStack);
                if (!Supported) return "";
                if (shouldStack) return StackFraction(numerator, denominator);
                var top = numerator.Trim();
                var bottom = denominator.Trim();
                if (!top.All(character => char.IsLetterOrDigit(character) || character == '.') || top.Contains('/')) top = $"({top})";
                if (!(bottom.All(character => char.IsAsciiDigit(character) || character == '.') || bottom.Length == 1) || bottom.Contains('/')) bottom = $"({bottom})";
                return $"{top}/{bottom}";
            }
            if (command == "sqrt")
            {
                var degree = ParseOptionalArgument();
                var value = ParseArgument().Trim();
                if (!Supported) return "";
                var prefix = degree switch { null or "2" => "√", "3" => "∛", "4" => "∜", _ => FormatScript(degree, true) + "√" };
                return prefix + (value.Length == 1 || value.All(char.IsLetterOrDigit) ? value : $"({value})");
            }
            if (command == "mathbb")
            {
                var value = ParseArgument();
                return string.Concat(value.Select(character => character switch
                {
                    'C' => "ℂ",
                    'H' => "ℍ",
                    'N' => "ℕ",
                    'P' => "ℙ",
                    'Q' => "ℚ",
                    'R' => "ℝ",
                    'Z' => "ℤ",
                    _ => character.ToString()
                }));
            }
            if (command is "text" or "mathrm" or "mathbf" or "mathit" or "mathnormal" or "operatorname" or "mbox" or "boxed" or "fbox")
            {
                var value = ParseArgument().Trim();
                if (command is "boxed" or "fbox") return $"[{value}]";
                if (command is "operatorname" && _position < source.Length && source[_position] == '*') _position++;
                return value;
            }
            if (command is "mod" or "bmod") return " mod ";
            if (command is "pmatrix" or "begin") return ParseEnvironment(command == "begin");
            if (command == "end")
            {
                Supported = false;
                return "";
            }
            if (Symbols.TryGetValue(command, out var symbol))
                return SpacedSymbols.Contains(command) ? $" {symbol} " : symbol;
            if (command is "sin" or "cos" or "tan" or "log" or "ln" or "exp" or "min" or "max" or "lim" or "det" or "dim" or "ker" or "gcd")
                return command;

            Supported = false;
            return "";
        }

        private void AppendScripts(StringBuilder result, char initial)
        {
            TrimEnd(result);
            _position++;
            var first = ParseScriptArgument();
            var second = "";
            var kind = initial;
            var nextPosition = _position;
            SkipWhitespace();
            if (_position < source.Length && source[_position] is '^' or '_' && source[_position] != initial)
            {
                kind = source[_position++];
                second = ParseScriptArgument();
            }
            else _position = nextPosition;
            var formattedFirst = FormatScript(first, initial == '^');
            var formattedSecond = second.Length == 0 ? "" : FormatScript(second, kind == '^');
            result.Append(formattedFirst).Append(formattedSecond);
        }

        private string ParseScriptArgument() => ParseArgument(allowStackedFraction: false);

        private string ParseArgument(bool allowStackedFraction = true)
        {
            if (!EnterNesting()) return "";
            var previousStackFractions = _stackFractions;
            _stackFractions &= allowStackedFraction;
            try
            {
                return ParseArgumentValue();
            }
            finally
            {
                _stackFractions = previousStackFractions;
                _depth--;
            }
        }

        private string ParseArgumentValue()
        {
            SkipWhitespace();
            if (_position >= source.Length)
            {
                Supported = false;
                return "";
            }
            if (source[_position] == '{')
            {
                _position++;
                return ParseSequence(group: true);
            }
            if (source[_position] == '\\')
            {
                _position++;
                return ParseCommand();
            }
            return source[_position++].ToString();
        }

        private string ParseOptionalArgument()
        {
            SkipWhitespace();
            if (_position >= source.Length || source[_position] != '[') return "2";
            var end = source.IndexOf(']', _position + 1);
            if (end < 0)
            {
                Supported = false;
                return "2";
            }
            var value = source[(_position + 1)..end];
            _position = end + 1;
            return value;
        }

        private string AppendNot()
        {
            var value = ParseArgument().Trim();
            return value switch
            {
                "=" => " ≠ ",
                "<" => " ≮ ",
                ">" => " ≯ ",
                "∈" => " ∉ ",
                "≤" => " ≰ ",
                "≥" => " ≱ ",
                _ => value.Length == 0 ? "" : $" {value[0]}\u0338{value[1..]} "
            };
        }

        private string ParseEnvironment(bool hasName)
        {
            if (!hasName) return "pmatrix";
            var name = ReadRawGroup();
            var endToken = $"\\end{{{name}}}";
            var end = source.IndexOf(endToken, _position, StringComparison.Ordinal);
            if (string.IsNullOrEmpty(name) || end < 0)
            {
                Supported = false;
                return "";
            }
            var body = source[_position..end];
            _position = end + endToken.Length;
            var rows = new List<string>();
            foreach (var row in body.Split("\\\\", StringSplitOptions.None))
            {
                var nested = new Parser(row, false, _depth);
                var parsed = nested.Parse();
                if (!nested.Supported)
                {
                    Supported = false;
                    return "";
                }
                var rendered = Normalize(parsed).Replace("&", "", StringComparison.Ordinal);
                if (rendered.Length > 0) rows.Add(rendered);
            }
            if (name == "aligned") return string.Join('\n', rows);
            if (name is "matrix" or "pmatrix" or "bmatrix")
            {
                if (name == "matrix") return string.Join('\n', rows);
                var left = name == "pmatrix" ? "(" : "[";
                var right = name == "pmatrix" ? ")" : "]";
                return string.Join('\n', rows.Select((row, index) => $"{left}{row}{right}"));
            }
            Supported = false;
            return "";
        }

        private string ReadRawGroup()
        {
            SkipWhitespace();
            if (_position >= source.Length || source[_position] != '{') return "";
            var start = ++_position;
            var depth = 1;
            while (_position < source.Length)
            {
                if (source[_position] == '\\') { _position += Math.Min(2, source.Length - _position); continue; }
                if (source[_position] == '{') depth++;
                if (source[_position] == '}' && --depth == 0)
                {
                    var value = source[start.._position];
                    _position++;
                    return value;
                }
                _position++;
            }
            Supported = false;
            return "";
        }

        private void SkipWhitespace()
        {
            while (_position < source.Length && char.IsWhiteSpace(source[_position])) _position++;
        }

        private bool EnterNesting()
        {
            // Nested environment parsers share the active recursion budget.
            if (!Supported || _depth >= MaximumNestingDepth)
            {
                Supported = false;
                return false;
            }
            _depth++;
            return true;
        }

        private static string FormatScript(string value, bool superscript)
        {
            value = string.Concat(value.Trim().Where(character => !char.IsWhiteSpace(character)));
            var map = superscript ? Superscripts : Subscripts;
            var output = new StringBuilder();
            foreach (var character in value)
            {
                if (!map.TryGetValue(character, out var rendered))
                {
                    var prefix = superscript ? "^" : "_";
                    var runeCount = value.EnumerateRunes().Count();
                    return runeCount == 1 || !superscript && value.All(char.IsAsciiLetter)
                        ? prefix + value
                        : $"{prefix}({value})";
                }
                output.Append(rendered);
            }
            return output.ToString();
        }

        private static string StackFraction(string numerator, string denominator)
        {
            numerator = numerator.Trim();
            denominator = denominator.Trim();
            var width = Math.Max(TerminalTextLayout.Width(numerator), TerminalTextLayout.Width(denominator));
            var rule = new string('─', Math.Max(1, width));
            return Center(numerator, width) + "\n" + rule + "\n" + Center(denominator, width);
        }

        private static string Center(string value, int width)
        {
            var padding = Math.Max(0, width - TerminalTextLayout.Width(value));
            return new string(' ', padding / 2) + value + new string(' ', padding - padding / 2);
        }

        private static void TrimEnd(StringBuilder builder)
        {
            while (builder.Length > 0 && char.IsWhiteSpace(builder[^1])) builder.Length--;
        }
    }
}
