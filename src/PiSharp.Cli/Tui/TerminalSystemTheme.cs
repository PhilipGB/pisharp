namespace PiSharp.Cli.Tui;

/// <summary>Builds Pi's terminal-derived system palette with OKHSL hues and contrast-constrained roles.</summary>
internal static class TerminalSystemTheme
{
    private sealed record Family(double Hue, double MinimumSaturation, double MaximumSaturation, int PaletteSlot);
    private sealed record Curve(double[] Coefficients, double MinimumSurface, double MaximumSurface);
    private sealed record Rule(string Token, string[] Surfaces, Level Level);
    private enum Level
    {
        Panel, Track, Thinking0, Thinking1, Thinking2, Thinking3, Thinking4, Thinking5, Thinking6,
        Subtle, Thumb, Readable, Emphasis, TextOnPanel, Text
    }

    private const double BodyContrast = 4.5;
    private static readonly TerminalTheme.Rgb s_black = new(0, 0, 0);
    private static readonly TerminalTheme.Rgb s_white = new(255, 255, 255);
    private static readonly Dictionary<string, Family> s_families = CreateFamilies();
    private static readonly Dictionary<string, string> s_tokenFamilies = CreateTokenFamilies();
    private static readonly Dictionary<string, int> s_tokenPaletteSlots = new(StringComparer.Ordinal)
    {
        ["syntaxString"] = 2,
        ["syntaxNumber"] = 5,
        ["searchMatchBg"] = 3
    };
    private static readonly HashSet<string> s_panels = new(StringComparer.Ordinal)
    {
        "userMessageBg", "toolPendingBg", "toolSuccessBg", "toolErrorBg", "selectedBg", "searchMatchBg", "customMessageBg"
    };
    private static readonly HashSet<string> s_foregroundTokens = new(StringComparer.Ordinal)
    {
        "text", "userMessageText", "toolTitle"
    };
    private static readonly string[] s_thinkingTokens =
    ["thinkingOff", "thinkingMinimal", "thinkingLow", "thinkingMedium", "thinkingHigh", "thinkingXhigh", "thinkingMax"];
    private static readonly Level[] s_thinkingLevels =
    [Level.Thinking0, Level.Thinking1, Level.Thinking2, Level.Thinking3, Level.Thinking4, Level.Thinking5, Level.Thinking6];
    private static readonly Dictionary<(Level, string), Curve> s_levels = CreateLevels();
    private static readonly Rule[] s_rules = CreateRules();
    private static readonly string[] s_solveOrder = CreateSolveOrder();

    public static TerminalTheme Create(TerminalColorMode mode, TerminalColorState terminal, string appearanceHint)
    {
        var generated = Generate(terminal, appearanceHint);
        return TerminalTheme.FromSystemTheme(generated.Appearance, mode, generated.Colors,
            terminal.Foreground, terminal.Background, generated.DimTokens);
    }

    public static string DetectAppearance(TerminalColorState terminal, string? colorFgBg)
    {
        if (terminal.Background is { } background) return DetectAppearance(background, terminal.Foreground);
        if (terminal.AppearanceReport is "dark" or "light") return terminal.AppearanceReport;
        return DetectColorFgBg(colorFgBg) ?? "dark";
    }

    public static string? DetectColorFgBg(string? value)
    {
        var background = value?.Split(';').LastOrDefault()?.Trim();
        if (string.IsNullOrEmpty(background) || background.Length > 2 ||
            !int.TryParse(background, out var index) || index is < 0 or > 15) return null;
        return index <= 6 || index == 8 ? "dark" : "light";
    }

    public static double Contrast(TerminalTheme.Rgb first, TerminalTheme.Rgb second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static GeneratedTheme Generate(TerminalColorState terminal, string appearanceHint)
    {
        var saturation = 1d;
        if (terminal.Background is not { } background)
            return IndexedColors(saturation, appearanceHint);

        var palette = terminal.Palette is { Count: 16 }
            ? terminal.Palette.Select(TerminalColorSpace.RgbToOkhsl).ToArray()
            : null;
        var appearance = DetectAppearance(background, terminal.Foreground);
        var lighter = appearance == "dark";
        var extreme = lighter ? 1d : 0d;
        var backgroundLightness = TerminalColorSpace.OklabLightness(background);

        TerminalTheme.Rgb Paint(string token, double oklabLightness)
        {
            var lightness = TerminalColorSpace.OklabToOkhslLightness(oklabLightness);
            var family = s_families[s_tokenFamilies[token]];
            if (palette is null)
            {
                var colorSaturation = family.MinimumSaturation +
                    (family.MaximumSaturation - family.MinimumSaturation) * BellWeight(lightness);
                return TerminalColorSpace.OkhslToRgb(family.Hue, colorSaturation * saturation, lightness);
            }

            var slot = s_tokenPaletteSlots.TryGetValue(token, out var tokenSlot) ? tokenSlot : family.PaletteSlot;
            return Anchored(palette[slot], family, lightness, saturation);
        }

        double? Target(Level level, double surfaceLightness, double relaxation)
        {
            var curve = s_levels[(level, appearance)];
            var reached = surfaceLightness < curve.MinimumSurface || surfaceLightness > curve.MaximumSurface
                ? (double?)null
                : Evaluate(curve.Coefficients, surfaceLightness);
            if (reached is null && relaxation == 0) return null;
            var floorLevel = appearance == "dark" ? Level.Readable : Level.Subtle;
            var floorCurve = s_levels[(floorLevel, appearance)];
            var floor = surfaceLightness < floorCurve.MinimumSurface || surfaceLightness > floorCurve.MaximumSurface
                ? extreme
                : Evaluate(floorCurve.Coefficients, surfaceLightness);
            var distance = (reached ?? extreme) - surfaceLightness;
            var floorDistance = floor - surfaceLightness;
            var compressed = Math.Abs(distance) > Math.Abs(floorDistance)
                ? distance - (distance - floorDistance) * Math.Min(relaxation, 1)
                : distance;
            return surfaceLightness + compressed * (1 - Math.Max(0, relaxation - 1));
        }

        bool IsReadablePanel(TerminalTheme.Rgb color) => Contrast(lighter ? s_white : s_black, color) >= BodyContrast;

        TerminalTheme.Rgb LimitPanel(string token, double lightness)
        {
            var candidate = Paint(token, lightness);
            if (IsReadablePanel(candidate)) return candidate;
            var low = backgroundLightness;
            var high = lightness;
            for (var index = 0; index < 20; index++)
            {
                var middle = (low + high) / 2;
                if (IsReadablePanel(Paint(token, middle))) low = middle;
                else high = middle;
            }
            return Paint(token, low);
        }

        Dictionary<string, TerminalTheme.Rgb>? Solve(double relaxation)
        {
            var colors = new Dictionary<string, TerminalTheme.Rgb>(StringComparer.Ordinal) { ["background"] = background };
            foreach (var token in s_solveOrder)
            {
                var targets = new List<double>();
                foreach (var rule in s_rules.Where(rule => rule.Token == token))
                {
                    foreach (var surface in rule.Surfaces)
                    {
                        var surfaceColor = colors.TryGetValue(surface, out var value) ? value : background;
                        var target = Target(rule.Level, TerminalColorSpace.OklabLightness(surfaceColor), relaxation);
                        if (target is null || target < 0 || target > 1) return null;
                        targets.Add(target.Value);
                    }
                }
                if (targets.Count == 0) continue;
                var lightness = lighter ? targets.Max() : targets.Min();
                colors[token] = s_panels.Contains(token) ? LimitPanel(token, lightness) : Paint(token, lightness);
            }
            return colors;
        }

        var relaxation = 0d;
        var solved = Solve(relaxation);
        if (solved is null)
        {
            var low = 0d;
            var high = 2d;
            solved = Solve(high)!;
            for (var index = 0; index < 20; index++)
            {
                var middle = (low + high) / 2;
                var attempt = Solve(middle);
                if (attempt is not null) { high = middle; solved = attempt; }
                else low = middle;
            }
            relaxation = high;
        }

        List<TerminalTheme.Rgb> SurfacesOf(string token) => s_rules.Where(rule => rule.Token == token)
            .SelectMany(rule => rule.Surfaces.Select(surface => solved.TryGetValue(surface, out var color) ? color : background))
            .ToList();

        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var token in s_tokenFamilies.Keys)
            result[token] = solved.TryGetValue(token, out var color) ? Hex(color) : "";

        foreach (var token in s_foregroundTokens)
        {
            var surfaces = SurfacesOf(token);
            var text = solved.TryGetValue(token, out var color) ? color : (TerminalTheme.Rgb?)null;
            if (terminal.Foreground is { } foreground)
            {
                var targets = surfaces.Select(surface => Target(Level.Emphasis,
                    TerminalColorSpace.OklabLightness(surface), relaxation)).ToArray();
                if (targets.All(value => value is >= 0 and <= 1))
                {
                    var required = lighter ? targets.Max(value => value!.Value) : targets.Min(value => value!.Value);
                    var foregroundLightness = TerminalColorSpace.OklabLightness(foreground);
                    if (lighter ? foregroundLightness >= required : foregroundLightness <= required)
                    {
                        result[token] = "";
                        continue;
                    }
                    var source = TerminalColorSpace.RgbToOkhsl(foreground);
                    text = Anchored(source, s_families["neutral"], TerminalColorSpace.OklabToOkhslLightness(required), saturation);
                }
            }
            if (text is { } textColor)
                result[token] = Hex(WithTextContrast(textColor, surfaces, lighter));
        }
        return new(result, [], appearance);
    }

    private static GeneratedTheme IndexedColors(double saturation, string appearance)
    {
        var colors = new Dictionary<string, object>(StringComparer.Ordinal);
        var dim = new List<string>();
        foreach (var (token, familyName) in s_tokenFamilies)
        {
            var family = s_families[familyName];
            if (s_panels.Contains(token))
            {
                colors[token] = "";
                continue;
            }
            var neutral = familyName == "neutral";
            colors[token] = !neutral && saturation > 0
                ? (s_tokenPaletteSlots.TryGetValue(token, out var slot) ? slot : family.PaletteSlot)
                : "";
            if (neutral && !s_foregroundTokens.Contains(token)) dim.Add(token);
        }
        return new(colors, dim, appearance);
    }

    private static TerminalTheme.Rgb Anchored((double Hue, double Saturation, double Lightness) source,
        Family family, double lightness, double saturation)
    {
        var anchor = SaturationCurve(family, source.Lightness);
        var falloff = anchor > 0 ? Math.Min(1, SaturationCurve(family, lightness) / anchor) : 1;
        return TerminalColorSpace.OkhslToRgb(source.Hue, source.Saturation * falloff * saturation, lightness);
    }

    private static TerminalTheme.Rgb WithTextContrast(TerminalTheme.Rgb color,
        IReadOnlyList<TerminalTheme.Rgb> surfaces, bool lighter)
    {
        bool Meets(TerminalTheme.Rgb candidate) => surfaces.All(surface => Contrast(candidate, surface) >= BodyContrast);
        if (Meets(color)) return color;
        var source = TerminalColorSpace.RgbToOkhsl(color);
        TerminalTheme.Rgb At(double lightness) => TerminalColorSpace.OkhslToRgb(source.Hue, source.Saturation, lightness);
        var extreme = lighter ? 1d : 0d;
        if (!Meets(At(extreme))) return At(extreme);
        var low = source.Lightness;
        var high = extreme;
        for (var index = 0; index < 20; index++)
        {
            var middle = (low + high) / 2;
            if (Meets(At(middle))) high = middle;
            else low = middle;
        }
        return At(high);
    }

    private static string DetectAppearance(TerminalTheme.Rgb background, TerminalTheme.Rgb? foreground)
    {
        var whiteContrast = Contrast(s_white, background);
        var blackContrast = Contrast(s_black, background);
        if (foreground is { } text)
        {
            var foregroundLightness = TerminalColorSpace.OklabLightness(text);
            var backgroundLightness = TerminalColorSpace.OklabLightness(background);
            if (Math.Abs(foregroundLightness - backgroundLightness) > 0.05)
            {
                var appearance = foregroundLightness > backgroundLightness ? "dark" : "light";
                var best = appearance == "dark" ? whiteContrast : blackContrast;
                if (best >= BodyContrast) return appearance;
            }
        }
        return whiteContrast >= blackContrast ? "dark" : "light";
    }

    private static double RelativeLuminance(TerminalTheme.Rgb color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static double Evaluate(double[] coefficients, double value)
    {
        var result = 0d;
        for (var power = 0; power < coefficients.Length; power++) result += coefficients[power] * Math.Pow(value, power);
        return result;
    }

    private static double BellWeight(double lightness)
    {
        static double Gaussian(double value) => Math.Exp(-Math.Pow(value - 0.5, 2) / (2 * 0.25 * 0.25));
        return (Gaussian(lightness) - Gaussian(0)) / (1 - Gaussian(0));
    }

    private static double SaturationCurve(Family family, double lightness)
    {
        var floor = family.MaximumSaturation > 0 ? family.MinimumSaturation / family.MaximumSaturation : 1;
        return floor + (1 - floor) * BellWeight(lightness);
    }

    private static string Hex(TerminalTheme.Rgb color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    private static Dictionary<string, Family> CreateFamilies() => new(StringComparer.Ordinal)
    {
        ["neutral"] = new(231.49, 0.02, 0.08, 8),
        ["blue"] = new(231.49, 0.1, 0.68, 4),
        ["green"] = new(158.68, 0.1, 0.76, 2),
        ["red"] = new(20, 0.1, 0.92, 1),
        ["yellow"] = new(82.36, 0.5, 1, 3),
        ["orange"] = new(52, 0.12, 0.85, 3),
        ["violet"] = new(295, 0.2, 0.6, 5),
        ["calamine"] = new(202.43, 0.1, 0.74, 6),
        ["thinkingSlate"] = new(231.49, 0.08, 0.2, 4),
        ["thinkingBlue"] = new(231.49, 0.2, 0.45, 4),
        ["thinkingPeriwinkle"] = new(263.25, 0.3, 0.6, 6),
        ["thinkingViolet"] = new(295, 0.4, 0.75, 5),
        ["thinkingMagenta"] = new(337.5, 0.5, 0.85, 13),
        ["thinkingRed"] = new(20, 0.95, 1, 1)
    };

    private static Dictionary<string, string> CreateTokenFamilies() => new(StringComparer.Ordinal)
    {
        ["selectedBg"] = "blue",
        ["searchMatchBg"] = "orange",
        ["userMessageBg"] = "blue",
        ["customMessageBg"] = "violet",
        ["toolPendingBg"] = "neutral",
        ["toolSuccessBg"] = "green",
        ["toolErrorBg"] = "red",
        ["text"] = "neutral",
        ["userMessageText"] = "neutral",
        ["customMessageText"] = "neutral",
        ["toolTitle"] = "neutral",
        ["syntaxOperator"] = "neutral",
        ["syntaxPunctuation"] = "neutral",
        ["muted"] = "neutral",
        ["dim"] = "neutral",
        ["thinkingText"] = "neutral",
        ["toolOutput"] = "neutral",
        ["mdLinkUrl"] = "neutral",
        ["mdQuote"] = "neutral",
        ["mdQuoteBorder"] = "neutral",
        ["mdHr"] = "neutral",
        ["mdCodeBlockBorder"] = "neutral",
        ["toolDiffContext"] = "neutral",
        ["syntaxComment"] = "neutral",
        ["scrollbarTrack"] = "neutral",
        ["scrollbarThumb"] = "neutral",
        ["searchMatchText"] = "neutral",
        ["borderMuted"] = "neutral",
        ["accent"] = "violet",
        ["borderAccent"] = "violet",
        ["customMessageLabel"] = "violet",
        ["mdCode"] = "violet",
        ["mdListBullet"] = "violet",
        ["syntaxType"] = "violet",
        ["border"] = "blue",
        ["mdLink"] = "blue",
        ["syntaxKeyword"] = "blue",
        ["syntaxVariable"] = "calamine",
        ["success"] = "green",
        ["mdCodeBlock"] = "green",
        ["toolDiffAdded"] = "green",
        ["bashMode"] = "green",
        ["syntaxNumber"] = "green",
        ["error"] = "red",
        ["toolDiffRemoved"] = "red",
        ["warning"] = "yellow",
        ["mdHeading"] = "yellow",
        ["syntaxFunction"] = "yellow",
        ["syntaxString"] = "orange",
        ["thinkingOff"] = "neutral",
        ["thinkingMinimal"] = "thinkingSlate",
        ["thinkingLow"] = "thinkingBlue",
        ["thinkingMedium"] = "thinkingPeriwinkle",
        ["thinkingHigh"] = "thinkingViolet",
        ["thinkingXhigh"] = "thinkingMagenta",
        ["thinkingMax"] = "thinkingRed"
    };

    private static Dictionary<(Level, string), Curve> CreateLevels()
    {
        var result = new Dictionary<(Level, string), Curve>();
        void Add(Level level, double[] dark, double darkMin, double darkMax,
            double[] light, double lightMin, double lightMax)
        {
            result[(level, "dark")] = new(dark, darkMin, darkMax);
            result[(level, "light")] = new(light, lightMin, lightMax);
        }
        Add(Level.Panel, [0.29131, -0.39746, 2.33185, -0.85524, -1.2076, 0.86276], 0, 0.979,
            [-3.74073, 27.94549, -78.44258, 112.6798, -79.60015, 22.11277], 0.348, 1);
        Add(Level.Track, [0.39028, -0.23015, 0.83573, 2.43829, -4.38292, 2.01582], 0, 0.946,
            [-5.24921, 38.37322, -107.28833, 152.10005, -106.17127, 29.18061], 0.368, 1);
        Add(Level.Thinking0, [0.52988, -0.05809, -0.30924, 4.63567, -6.52933, 2.89108], 0, 0.873,
            [-28.27749, 182.85284, -469.62416, 603.15916, -384.59976, 97.35147], 0.51, 1);
        Add(Level.Thinking1, [0.55278, -0.03667, -0.45659, 4.95347, -6.90265, 3.0706], 0, 0.858,
            [-37.10484, 235.86282, -596.62344, 754.3633, -474.00763, 118.3551], 0.535, 1);
        Add(Level.Thinking2, [0.57486, -0.01765, -0.58987, 5.25227, -7.27175, 3.25532], 0, 0.842,
            [-59.89653, 377.05024, -945.07843, 1182.03145, -734.96375, 181.68658], 0.556, 1);
        Add(Level.Thinking3, [0.59621, -0.00062, -0.71148, 5.53588, -7.6392, 3.44606], 0, 0.827,
            [-72.07122, 445.84082, -1099.57352, 1353.88793, -829.53392, 202.26164], 0.58, 1);
        Add(Level.Thinking4, [0.61691, 0.01462, -0.82288, 5.80651, -8.00641, 3.64333], 0, 0.811,
            [-110.14338, 674.21488, -1645.75941, 2004.32367, -1215.15899, 293.3183], 0.6, 1);
        Add(Level.Thinking5, [0.63702, 0.02826, -0.92498, 6.06465, -8.37246, 3.84651], 0, 0.795,
            [-175.47701, 1063.54495, -2570.70594, 3098.80776, -1860.15527, 444.76392], 0.62, 1);
        Add(Level.Thinking6, [0.65658, 0.04044, -1.01835, 6.30989, -8.73529, 4.05439], 0, 0.779,
            [-183.81712, 1094.70055, -2602.68539, 3088.71276, -1826.91131, 430.75931], 0.643, 1);
        Add(Level.Subtle, [0.56762, -0.02475, -0.5383, 5.12628, -7.10931, 3.17324], 0, 0.848,
            [-232.85459, 1376.54473, -3249.11801, 3827.91186, -2248.29472, 526.55751], 0.657, 1);
        Add(Level.Thumb, [0.60323, 0.00278, -0.73328, 5.57157, -7.68067, 3.46933], 0, 0.823,
            [-82.89897, 511.01355, -1255.98095, 1540.76821, -940.68087, 228.58523], 0.586, 1);
        Add(Level.Readable, [0.66937, 0.04704, -1.06871, 6.43941, -8.9332, 4.17229], 0, 0.77,
            [-1554.52576, 8733.56817, -19604.93507, 21977.72696, -12300.99599, 2749.81288], 0.751, 1);
        Add(Level.Emphasis, [0.7303, 0.07695, -1.31626, 7.1681, -10.14436, 4.92846], 0, 0.712,
            [-4948.31942, 26870.91986, -58334.48399, 63280.17197, -34298.01053, 7430.30146], 0.811, 1);
        Add(Level.TextOnPanel, [0.86713, 0.05232, -0.89428, 4.79014, -5.5432, 1.75023], 0, 0.542,
            [-8570.89457, 43954.60805, -90084.00702, 92220.6791, -47152.15802, 9632.27113], 0.867, 1);
        Add(Level.Text, [0.89242, 0.02311, -0.44862, 2.34417, -0.06084, -2.63844], 0, 0.5,
            [-2004.67048, 6664.47299, -6060.70202, -1792.61209, 5133.82359, -1939.85583], 0.894, 1);
        return result;
    }

    private static Rule[] CreateRules()
    {
        var toolPanels = new[] { "toolPendingBg", "toolSuccessBg", "toolErrorBg" };
        var messagePanels = new[] { "userMessageBg", "customMessageBg" };
        var panels = new[] { "userMessageBg", "toolPendingBg", "toolSuccessBg", "toolErrorBg", "selectedBg", "searchMatchBg", "customMessageBg" };
        var rules = new List<Rule>();
        void AddEach(IEnumerable<string> tokens, string[] surfaces, Level level)
        {
            rules.AddRange(tokens.Select(token => new Rule(token, surfaces, level)));
        }
        AddEach(panels, ["background"], Level.Panel);
        rules.Add(new("text", ["background"], Level.Text));
        rules.Add(new("text", ["selectedBg"], Level.TextOnPanel));
        rules.Add(new("userMessageText", ["userMessageBg"], Level.TextOnPanel));
        rules.Add(new("toolTitle", toolPanels, Level.TextOnPanel));
        AddEach(["accent", "success", "error", "warning"], ["background", "selectedBg", .. toolPanels], Level.Readable);
        rules.Add(new("muted", ["background", "selectedBg", "customMessageBg", .. toolPanels], Level.Readable));
        rules.Add(new("dim", ["background", "selectedBg", "customMessageBg", .. toolPanels], Level.Subtle));
        rules.Add(new("thinkingText", ["background"], Level.Readable));
        rules.Add(new("customMessageText", ["customMessageBg", .. toolPanels], Level.Readable));
        rules.Add(new("customMessageLabel", ["background", "customMessageBg", "selectedBg", .. toolPanels], Level.Readable));
        rules.Add(new("toolOutput", ["background", .. toolPanels], Level.Readable));
        AddEach(["mdHeading", "mdLink", "mdLinkUrl", "mdCode", "mdQuote", "mdCodeBlockBorder", "mdListBullet"],
            ["background", .. messagePanels], Level.Readable);
        rules.Add(new("mdCodeBlock", ["background", .. messagePanels, .. toolPanels], Level.Readable));
        AddEach(["toolDiffAdded", "toolDiffRemoved", "toolDiffContext"], ["background", .. toolPanels], Level.Readable);
        AddEach(["syntaxComment", "syntaxKeyword", "syntaxFunction", "syntaxVariable", "syntaxString", "syntaxNumber",
                "syntaxType", "syntaxOperator", "syntaxPunctuation"], ["background", .. messagePanels, .. toolPanels], Level.Readable);
        rules.Add(new("searchMatchText", ["searchMatchBg"], Level.Readable));
        AddEach(["bashMode", "border", "borderAccent"], ["background"], Level.Readable);
        rules.Add(new("borderMuted", ["background"], Level.Subtle));
        AddEach(["mdQuoteBorder", "mdHr"], ["background", .. messagePanels, .. toolPanels], Level.Readable);
        rules.Add(new("scrollbarTrack", ["background"], Level.Track));
        rules.Add(new("scrollbarThumb", ["scrollbarTrack"], Level.Thumb));
        for (var index = 0; index < s_thinkingTokens.Length; index++)
            rules.Add(new(s_thinkingTokens[index], ["background"], s_thinkingLevels[index]));
        return rules.ToArray();
    }

    private static string[] CreateSolveOrder()
    {
        var ordered = new List<string>();
        void Visit(string token)
        {
            if (ordered.Contains(token, StringComparer.Ordinal)) return;
            foreach (var rule in s_rules.Where(rule => rule.Token == token))
                foreach (var surface in rule.Surfaces)
                    if (surface != "background") Visit(surface);
            ordered.Add(token);
        }
        foreach (var rule in s_rules) Visit(rule.Token);
        return ordered.ToArray();
    }

    private sealed record GeneratedTheme(Dictionary<string, object> Colors, IReadOnlyList<string> DimTokens, string Appearance);
}
