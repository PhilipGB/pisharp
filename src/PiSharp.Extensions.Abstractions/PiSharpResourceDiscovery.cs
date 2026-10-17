namespace PiSharp.Extensions.Abstractions;

/// <summary>Identifies whether host resources are being discovered at startup or during reload.</summary>
public enum PiSharpResourceDiscoveryReason
{
    /// <summary>The host is discovering resources while starting.</summary>
    Startup,
    /// <summary>The host is discovering resources while reloading.</summary>
    Reload
}

/// <summary>Context supplied while an extension contributes resource paths.</summary>
/// <param name="WorkingDirectory">The active project working directory.</param>
/// <param name="Reason">Whether discovery runs at startup or reload.</param>
public sealed record PiSharpResourceDiscoveryContext(
    string WorkingDirectory,
    PiSharpResourceDiscoveryReason Reason);

/// <summary>Skill, prompt-template, and theme paths contributed by an extension.</summary>
/// <param name="SkillPaths">Skill files or directories to pass to the existing resource catalog.</param>
/// <param name="PromptPaths">Prompt files or directories to pass to the existing resource catalog.</param>
/// <param name="ThemePaths">Theme files or directories to pass to the existing resource catalog.</param>
public sealed record PiSharpResourceDiscoveryResult(
    IReadOnlyList<string>? SkillPaths = null,
    IReadOnlyList<string>? PromptPaths = null,
    IReadOnlyList<string>? ThemePaths = null);

/// <summary>Discovers resource paths contributed by a compiled extension.</summary>
/// <param name="context">Working directory and discovery reason.</param>
/// <param name="cancellationToken">The cancellation token for the owning resource discovery pass.</param>
public delegate Task<PiSharpResourceDiscoveryResult?> PiSharpResourceDiscoveryHandler(
    PiSharpResourceDiscoveryContext context,
    CancellationToken cancellationToken);
