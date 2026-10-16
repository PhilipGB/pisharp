using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace PiSharp.Runtime.Resources;

internal static class ResourceYamlScalar
{
    private const string IntegerPattern = @"^(?:[-+]?[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+)$";
    private const string FloatPattern = @"^(?:[-+]?(?:\.[0-9]+|[0-9]+\.[0-9]*|(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)[eE][-+]?[0-9]+)|[-+]?\.(?:inf|Inf|INF)|\.nan|\.NaN|\.NAN)$";

    internal static string? StringValue(Scalar? scalar) => scalar is null ? null : Value(scalar) as string;
    internal static bool BooleanTrue(Scalar? scalar) => scalar is not null && Value(scalar) is true;

    internal static object? Value(Scalar scalar)
    {
        var value = scalar.Value;
        var tag = scalar.Tag.ToString();
        var implicitType = scalar.Tag.IsEmpty && scalar.Style == ScalarStyle.Plain;
        if ((implicitType || tag == "tag:yaml.org,2002:null") && IsNull(value)) return null;
        if ((implicitType || tag == "tag:yaml.org,2002:bool") && IsBoolean(value))
            return value is "true" or "True" or "TRUE";
        if ((implicitType || tag == "tag:yaml.org,2002:int") && Regex.IsMatch(value, IntegerPattern, RegexOptions.CultureInvariant))
        {
            if (value.StartsWith("0x", StringComparison.Ordinal))
                return (double)BigInteger.Parse("0" + value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            if (value.StartsWith("0o", StringComparison.Ordinal))
            {
                var number = BigInteger.Zero;
                foreach (var digit in value.AsSpan(2)) number = number * 8 + digit - '0';
                return (double)number;
            }
            return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        if ((implicitType || tag == "tag:yaml.org,2002:float") && Regex.IsMatch(value, FloatPattern, RegexOptions.CultureInvariant))
        {
            if (value.EndsWith("nan", StringComparison.OrdinalIgnoreCase)) return double.NaN;
            if (value.EndsWith("inf", StringComparison.OrdinalIgnoreCase))
                return value.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
            return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        return value;
    }

    internal static bool EqualKeys(object? value, object? other)
    {
        // Pi compares scalar key values with JavaScript strict equality, including unequal NaN values.
        if (value is double number && double.IsNaN(number)) return false;
        return Equals(value, other);
    }

    private static bool IsNull(string value) => value is "" or "~" or "null" or "Null" or "NULL";
    private static bool IsBoolean(string value) => value is "true" or "True" or "TRUE" or "false" or "False" or "FALSE";
}
