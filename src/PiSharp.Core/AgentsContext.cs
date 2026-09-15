namespace PiSharp.Core;

public sealed record AgentsContext(
    IReadOnlyList<string> Files,
    string Content);
