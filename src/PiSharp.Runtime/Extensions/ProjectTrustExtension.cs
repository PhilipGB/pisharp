using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

/// <summary>Information available to a native extension while deciding whether project resources may load.</summary>
public sealed record ProjectTrustExtensionContext(string WorkingDirectory, bool HasUserInterface);

public enum ProjectTrustExtensionDecision
{
    Undecided,
    Yes,
    No
}

public sealed record ProjectTrustExtensionResult(ProjectTrustExtensionDecision Decision, bool Remember = false);

public delegate Task<ProjectTrustExtensionResult?> ProjectTrustExtensionHandler(
    ProjectTrustExtensionContext context, CancellationToken cancellationToken);

internal sealed record RegisteredProjectTrustHandler(ProjectTrustExtensionHandler Handler,
    ResourceSourceInfo SourceInfo);

internal sealed record ProjectTrustExtensionError(string ExtensionPath, string Message);

internal sealed record ProjectTrustExtensionResolution(ProjectTrustExtensionResult? Result,
    IReadOnlyList<ProjectTrustExtensionError> Errors);
