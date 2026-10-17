using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

/// <summary>One compiled-extension flag and the source package that owns it.</summary>
public sealed record RegisteredExtensionFlag(string Name, string? Description, PiSharpExtensionFlagType Type,
    PiSharpExtensionFlagValue? DefaultValue, ResourceSourceInfo SourceInfo);

public sealed partial class ExtensionRegistration
{
    private readonly List<RegisteredExtensionFlag> _extensionFlags = [];

    /// <summary>Gets the currently registered extension flags in registration order.</summary>
    public IReadOnlyList<RegisteredExtensionFlag> ExtensionFlags => Array.AsReadOnly(_extensionFlags.ToArray());

    internal void AddFlag(string name, PiSharpExtensionFlagType type, string? description,
        PiSharpExtensionFlagValue? defaultValue)
    {
        if (string.IsNullOrWhiteSpace(name) || name[0] == '-' ||
            !name.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            throw new ArgumentException("Extension flag names must contain only ASCII letters, digits, or hyphens and omit the leading --.", nameof(name));
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        if (defaultValue is not null && defaultValue.Type != type)
            throw new ArgumentException($"Default for extension flag --{name} must be {type.ToString().ToLowerInvariant()}.", nameof(defaultValue));

        var sourceInfo = CurrentSourceInfo ?? throw new InvalidOperationException(
            "Extension flags can only be registered while an extension is being configured.");
        var existing = _extensionFlags.FindIndex(flag => flag.Name == name);
        if (existing >= 0)
        {
            var owner = _extensionFlags[existing];
            if (owner.SourceInfo.Path != sourceInfo.Path)
                throw new InvalidOperationException($"Extension flag --{name} from {sourceInfo.Path} conflicts with its registration from {owner.SourceInfo.Path}.");
            _extensionFlags[existing] = new(name, description, type, defaultValue, sourceInfo);
            return;
        }

        _extensionFlags.Add(new(name, description, type, defaultValue, sourceInfo));
    }
}
