namespace PiSharp.Runtime.Sessions;

/// <summary>Queued input not yet appended to model or canonical session history.</summary>
public sealed record PendingPrompts(IReadOnlyList<string> Steering, IReadOnlyList<string> FollowUp)
{
    public static PendingPrompts Empty { get; } = new([], []);
    public IReadOnlyList<string> InDeliveryOrder => [.. Steering, .. FollowUp];
}
