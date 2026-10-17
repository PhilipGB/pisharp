using PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

public sealed partial class ExtensionRegistration
{
    private readonly List<ExtensionShortcutInfo> _shortcuts = [];

    /// <summary>Returns registered shortcuts in extension load order, with their source ownership.</summary>
    public IReadOnlyList<ExtensionShortcutInfo> ExtensionShortcuts => _shortcuts.ToArray();

    internal void AddShortcut(PiSharpShortcutKey shortcut, PiSharpExtensionShortcutHandler handler,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!shortcut.IsValid)
            throw new ArgumentException("A shortcut must use a named terminal key and supported modifiers.", nameof(shortcut));

        var sourceInfo = _currentSourceInfo ?? SourceInfoFromHandler(handler);
        var registration = new ExtensionShortcutInfo(shortcut, description, handler, sourceInfo);
        var ownedIndex = _shortcuts.FindIndex(candidate => candidate.SourceInfo.Path == sourceInfo.Path &&
            candidate.Shortcut == shortcut);
        if (ownedIndex >= 0) _shortcuts[ownedIndex] = registration;
        else _shortcuts.Add(registration);
    }
}
