using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

/// <summary>A keyboard shortcut retained with the extension that registered it.</summary>
public sealed record ExtensionShortcutInfo(PiSharpShortcutKey Shortcut, string? Description,
    PiSharpExtensionShortcutHandler Handler, ResourceSourceInfo SourceInfo);
