/*
 * OKHSL conversion follows Björn Ottosson's reference implementation:
 * https://bottosson.github.io/posts/colorpicker/
 * Copyright (c) 2021 Björn Ottosson, used under the MIT license.
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
 * associated documentation files (the "Software"), to deal in the Software without restriction,
 * including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
 * and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
 * subject to the following conditions: the above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS",
 * WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED.
 */
namespace PiSharp.Cli.Tui;

/// <summary>Converts terminal RGB colors to and from perceptual OKLab and OKHSL spaces.</summary>
internal static class TerminalColorSpace
{
    private readonly record struct Lab(double L, double A, double B);
    private readonly record struct Vector(double X, double Y, double Z)
    {
        public double this[int index] => index switch { 0 => X, 1 => Y, 2 => Z, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
    }
    private readonly record struct Matrix(Vector Row0, Vector Row1, Vector Row2)
    {
        public Vector this[int index] => index switch { 0 => Row0, 1 => Row1, 2 => Row2, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
    }
    private readonly record struct SaturationFit(double X, double Y, double[] Coefficients);

    private static readonly Matrix s_linearSrgbToLms = new(
        new(0.4122214694707629, 0.5363325372617349, 0.0514459932675022),
        new(0.2119034958178251, 0.6806995506452344, 0.1073969535369405),
        new(0.0883024591900564, 0.2817188391361215, 0.6299787016738222));
    private static readonly Matrix s_lmsToLab = new(
        new(0.210454268309314, 0.793617774702305, -0.0040720430116193),
        new(1.9779985324311684, -2.42859224204858, 0.450593709617411),
        new(0.0259040424655478, 0.7827717124575296, -0.8086757549230774));
    private static readonly Matrix s_labToLms = new(
        new(1, 0.3963377773761749, 0.2158037573099136),
        new(1, -0.1055613458156586, -0.0638541728258133),
        new(1, -0.0894841775298119, -1.2914855480194092));
    private static readonly Matrix s_lmsToLinearSrgb = new(
        new(4.0767416360759583, -3.3077115392580629, 0.2309699031821043),
        new(-1.2684379732850315, 2.6097573492876882, -0.341319376002657),
        new(-0.0041960761386756, -0.7034186179359362, 1.7076146940746117));
    private static readonly SaturationFit[] s_saturationFits =
    [
        new(-1.8817031, -0.80936501, [1.19086277, 1.76576728, 0.59662641, 0.75515197, 0.56771245]),
        new(1.8144408, -1.19445267, [0.73956515, -0.45954404, 0.08285427, 0.12541073, -0.14503204]),
        new(0.13110758, 1.81333971, [1.35733652, -0.00915799, -1.1513021, -0.50559606, 0.00692167])
    ];
    private const double K1 = 0.206;
    private const double K2 = 0.03;
    private const double K3 = (1 + K1) / (1 + K2);

    public static TerminalTheme.Rgb OkhslToRgb(double hue, double saturation, double lightness)
    {
        var labLightness = OkhslToOklabLightness(lightness);
        var lab = new Lab(labLightness, 0, 0);
        if (labLightness > 0 && labLightness < 1 && saturation > 0)
        {
            var angle = 2 * Math.PI * (hue - Math.Floor(hue / 360) * 360) / 360;
            var a = Math.Cos(angle);
            var b = Math.Sin(angle);
            var (c0, cMid, cMax) = ChromaStops(labLightness, a, b);
            double chroma;
            if (saturation < 0.8)
            {
                var t = 1.25 * saturation;
                var k1 = 0.8 * c0;
                chroma = t * k1 / (1 - (1 - k1 / cMid) * t);
            }
            else
            {
                var t = 5 * (saturation - 0.8);
                var k1 = 0.2 * cMid * cMid * 1.25 * 1.25 / c0;
                chroma = cMid + t * k1 / (1 - (1 - k1 / (cMax - cMid)) * t);
            }
            lab = new(labLightness, chroma * a, chroma * b);
        }

        return LinearSrgbToRgb(OklabToLinearSrgb(lab));
    }

    public static (double Hue, double Saturation, double Lightness) RgbToOkhsl(TerminalTheme.Rgb color)
    {
        var lab = RgbToOklab(color);
        var chroma = Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
        var lightness = OklabToOkhslLightness(lab.L);
        if (chroma < 1e-9 || lightness <= 0 || lightness >= 1) return (0, 0, lightness);

        var hue = (Math.Atan2(lab.B, lab.A) * 180 / Math.PI + 360) % 360;
        var (c0, cMid, cMax) = ChromaStops(lab.L, lab.A / chroma, lab.B / chroma);
        double saturation;
        if (chroma < cMid)
        {
            var k1 = 0.8 * c0;
            saturation = 0.8 * (chroma / (k1 + (1 - k1 / cMid) * chroma));
        }
        else
        {
            var k1 = 0.2 * cMid * cMid * 1.25 * 1.25 / c0;
            var offset = chroma - cMid;
            saturation = 0.8 + 0.2 * (offset / (k1 + (1 - k1 / (cMax - cMid)) * offset));
        }
        return (hue, Math.Clamp(saturation, 0, 1), lightness);
    }

    public static (double Hue, double Chroma, double Lightness) RgbToOklch(TerminalTheme.Rgb color)
    {
        var lab = RgbToOklab(color);
        return ((Math.Atan2(lab.B, lab.A) * 180 / Math.PI + 360) % 360,
            Math.Sqrt(lab.A * lab.A + lab.B * lab.B), lab.L);
    }

    public static TerminalTheme.Rgb OklchToRgb(double lightness, double chroma, double hue)
    {
        var angle = hue * Math.PI / 180;
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);
        Vector At(double value) => OklabToLinearSrgb(new(lightness, value * cosine, value * sine));
        static bool InGamut(Vector value) => value.X is >= -1e-7 and <= 1.0000001 &&
            value.Y is >= -1e-7 and <= 1.0000001 && value.Z is >= -1e-7 and <= 1.0000001;
        var linear = At(chroma);
        if (InGamut(linear)) return LinearSrgbToRgb(linear);
        var low = 0d;
        var high = chroma;
        linear = At(0);
        for (var index = 0; index < 20; index++)
        {
            var middle = (low + high) / 2;
            var candidate = At(middle);
            if (InGamut(candidate)) { low = middle; linear = candidate; }
            else high = middle;
        }
        return LinearSrgbToRgb(linear);
    }

    public static double OklabLightness(TerminalTheme.Rgb color) => RgbToOklab(color).L;

    public static double OklabToOkhslLightness(double value) =>
        0.5 * (K3 * value - K1 + Math.Sqrt((K3 * value - K1) * (K3 * value - K1) + 4 * K2 * K3 * value));

    private static double OkhslToOklabLightness(double value) =>
        (value * value + K1 * value) / (K3 * (value + K2));

    private static (double C0, double CMid, double CMax) ChromaStops(double lightness, double a, double b)
    {
        var (cuspLightness, cuspChroma) = Cusp(a, b);
        var cMax = MaxChroma(a, b, lightness, cuspLightness, cuspChroma);
        var k = cMax / Math.Min(lightness * (cuspChroma / cuspLightness), (1 - lightness) * (cuspChroma / (1 - cuspLightness)));
        var midS = 0.11516993 + 1 / (7.4477897 + 4.1590124 * b + a * (-2.19557347 + 1.75198401 * b +
            a * (-2.13704948 - 10.02301043 * b + a * (-4.24894561 + 5.38770819 * b + 4.69891013 * a))));
        var midT = 0.11239642 + 1 / (1.6132032 - 0.68124379 * b + a * (0.40370612 + 0.90148123 * b +
            a * (-0.27087943 + 0.6122399 * b + a * (0.00299215 - 0.45399568 * b - 0.14661872 * a))));
        var cMid = 0.9 * k * Math.Sqrt(Math.Sqrt(1 / (1 / Math.Pow(lightness * midS, 4) + 1 / Math.Pow((1 - lightness) * midT, 4))));
        var c0 = Math.Sqrt(1 / (1 / Math.Pow(lightness * 0.4, 2) + 1 / Math.Pow((1 - lightness) * 0.8, 2)));
        return (c0, cMid, cMax);
    }

    private static (double Lightness, double Chroma) Cusp(double a, double b)
    {
        var saturation = MaxSaturation(a, b);
        var linear = OklabToLinearSrgb(new(1, saturation * a, saturation * b));
        var lightness = Math.Cbrt(1 / Math.Max(linear.X, Math.Max(linear.Y, linear.Z)));
        return (lightness, lightness * saturation);
    }

    private static double MaxChroma(double a, double b, double lightness, double cuspLightness, double cuspChroma)
    {
        if (lightness <= cuspLightness) return cuspChroma * lightness / cuspLightness;
        var t = cuspChroma * (lightness - 1) / (cuspLightness - 1);
        var slopes = LmsSlopes(a, b);
        var lms = new Vector(lightness + t * slopes.X, lightness + t * slopes.Y, lightness + t * slopes.Z);
        var first = new Vector(3 * slopes.X * lms.X * lms.X, 3 * slopes.Y * lms.Y * lms.Y, 3 * slopes.Z * lms.Z * lms.Z);
        var second = new Vector(6 * slopes.X * slopes.X * lms.X, 6 * slopes.Y * slopes.Y * lms.Y, 6 * slopes.Z * slopes.Z * lms.Z);
        var steps = new double[3];
        for (var index = 0; index < steps.Length; index++)
        {
            var row = s_lmsToLinearSrgb[index];
            var f = Dot(row, Cube(lms)) - 1;
            var f1 = Dot(row, first);
            var f2 = Dot(row, second);
            var u = f1 / (f1 * f1 - 0.5 * f * f2);
            steps[index] = u >= 0 ? -f * u : double.MaxValue;
        }
        return t + steps.Min();
    }

    private static double MaxSaturation(double a, double b)
    {
        var channel = 2;
        for (var index = 0; index < 2; index++)
        {
            if (s_saturationFits[index].X * a + s_saturationFits[index].Y * b > 1)
            {
                channel = index;
                break;
            }
        }
        var fit = s_saturationFits[channel];
        var coefficients = fit.Coefficients;
        var saturation = coefficients[0] + coefficients[1] * a + coefficients[2] * b + coefficients[3] * a * a + coefficients[4] * a * b;
        var slopes = LmsSlopes(a, b);
        var baseValues = new Vector(1 + saturation * slopes.X, 1 + saturation * slopes.Y, 1 + saturation * slopes.Z);
        var weights = s_lmsToLinearSrgb[channel];
        var f = Dot(weights, Cube(baseValues));
        var f1 = Dot(weights, new(3 * slopes.X * baseValues.X * baseValues.X,
            3 * slopes.Y * baseValues.Y * baseValues.Y, 3 * slopes.Z * baseValues.Z * baseValues.Z));
        var f2 = Dot(weights, new(6 * slopes.X * slopes.X * baseValues.X,
            6 * slopes.Y * slopes.Y * baseValues.Y, 6 * slopes.Z * slopes.Z * baseValues.Z));
        return saturation - f * f1 / (f1 * f1 - 0.5 * f * f2);
    }

    private static Vector LmsSlopes(double a, double b) => new(
        s_labToLms.Row0.Y * a + s_labToLms.Row0.Z * b,
        s_labToLms.Row1.Y * a + s_labToLms.Row1.Z * b,
        s_labToLms.Row2.Y * a + s_labToLms.Row2.Z * b);

    private static Vector OklabToLinearSrgb(Lab lab)
    {
        var l = lab.L + 0.3963377773761749 * lab.A + 0.2158037573099136 * lab.B;
        var m = lab.L - 0.1055613458156586 * lab.A - 0.0638541728258133 * lab.B;
        var s = lab.L - 0.0894841775298119 * lab.A - 1.2914855480194092 * lab.B;
        return Multiply(s_lmsToLinearSrgb, new(l * l * l, m * m * m, s * s * s));
    }

    private static Lab RgbToOklab(TerminalTheme.Rgb color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        var lms = Multiply(s_linearSrgbToLms, new(Linear(color.R), Linear(color.G), Linear(color.B)));
        var roots = new Vector(Math.Cbrt(lms.X), Math.Cbrt(lms.Y), Math.Cbrt(lms.Z));
        var lab = Multiply(s_lmsToLab, roots);
        return new(lab.X, lab.Y, lab.Z);
    }

    private static TerminalTheme.Rgb LinearSrgbToRgb(Vector linear)
    {
        static byte Channel(double value)
        {
            var encoded = value > 0.0031308 ? 1.055 * Math.Pow(value, 1 / 2.4) - 0.055 : 12.92 * value;
            return (byte)Math.Round(Math.Clamp(encoded, 0, 1) * 255);
        }
        return new(Channel(linear.X), Channel(linear.Y), Channel(linear.Z));
    }

    private static Vector Cube(Vector value) => new(value.X * value.X * value.X, value.Y * value.Y * value.Y, value.Z * value.Z * value.Z);
    private static double Dot(Vector left, Vector right) => left.X * right.X + left.Y * right.Y + left.Z * right.Z;
    private static Vector Multiply(Matrix matrix, Vector value) => new(Dot(matrix.Row0, value), Dot(matrix.Row1, value), Dot(matrix.Row2, value));
}
