namespace PiSharp.Extensions.Abstractions;

public partial interface IPiSharpExtensionBuilder
{
    /// <summary>Registers a callback that may cancel replacement of the active session.</summary>
    void AddSessionBeforeSwitchHandler(PiSharpExtensionSessionBeforeSwitchHandler handler);

    /// <summary>Registers a callback that may cancel a fork of the active session.</summary>
    void AddSessionBeforeForkHandler(PiSharpExtensionSessionBeforeForkHandler handler);

    /// <summary>Registers a callback that may cancel navigation within the active session tree.</summary>
    void AddSessionBeforeTreeHandler(PiSharpExtensionSessionBeforeTreeHandler handler);

    /// <summary>Observes session-tree navigation after the selected branch is persisted.</summary>
    void AddSessionTreeHandler(PiSharpExtensionSessionTreeHandler handler);

    /// <summary>Registers a callback that may cancel or supply a summary for canonical compaction.</summary>
    void AddSessionBeforeCompactHandler(PiSharpExtensionSessionBeforeCompactHandler handler);
}
