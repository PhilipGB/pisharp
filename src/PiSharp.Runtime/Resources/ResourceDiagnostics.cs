namespace PiSharp.Runtime.Resources;

public sealed record ResourceCollision(string ResourceType, string Name, string WinnerPath, string LoserPath);

public sealed record ResourceDiagnostic(string Type, string Message, string? Path = null,
    ResourceCollision? Collision = null);
