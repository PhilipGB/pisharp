namespace PiSharp.Extensions.Abstractions;

/// <summary>The supported value type for an extension command-line flag.</summary>
public enum PiSharpExtensionFlagType
{
    /// <summary>A flag which is either enabled or disabled.</summary>
    Boolean,
    /// <summary>A flag which carries one string value.</summary>
    String
}

/// <summary>A detached Boolean or string value returned by an extension flag lookup.</summary>
public sealed record PiSharpExtensionFlagValue
{
    private PiSharpExtensionFlagValue(PiSharpExtensionFlagType type, bool booleanValue, string? stringValue)
    {
        Type = type;
        _booleanValue = booleanValue;
        _stringValue = stringValue;
    }

    /// <summary>The value type.</summary>
    public PiSharpExtensionFlagType Type { get; }

    /// <summary>The Boolean value when <see cref="Type"/> is <see cref="PiSharpExtensionFlagType.Boolean"/>.</summary>
    public bool BooleanValue => Type == PiSharpExtensionFlagType.Boolean
        ? _booleanValue
        : throw new InvalidOperationException("The extension flag value is not Boolean.");

    /// <summary>The string value when <see cref="Type"/> is <see cref="PiSharpExtensionFlagType.String"/>.</summary>
    public string StringValue => Type == PiSharpExtensionFlagType.String
        ? _stringValue!
        : throw new InvalidOperationException("The extension flag value is not a string.");

    private readonly bool _booleanValue;
    private readonly string? _stringValue;

    /// <summary>Creates a Boolean flag value, including an explicit false value.</summary>
    public static PiSharpExtensionFlagValue FromBoolean(bool value) => new(PiSharpExtensionFlagType.Boolean, value, null);

    /// <summary>Creates a string flag value.</summary>
    public static PiSharpExtensionFlagValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(PiSharpExtensionFlagType.String, false, value);
    }
}

/// <summary>Adds extension-owned CLI flags to the compiled-extension registration contract.</summary>
public partial interface IPiSharpExtensionBuilder
{
    /// <summary>Registers a Boolean or string flag. The name omits the leading <c>--</c>.</summary>
    void RegisterFlag(string name, PiSharpExtensionFlagType type, string? description = null,
        PiSharpExtensionFlagValue? defaultValue = null);
}
