namespace PiSharp.Extensions.Abstractions;

/// <summary>Handles an extension keyboard shortcut with the current command and UI context.</summary>
public delegate Task PiSharpExtensionShortcutHandler(PiSharpExtensionCommandContext context,
    CancellationToken cancellationToken);
