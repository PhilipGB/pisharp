namespace PiSharp.Extensions.Abstractions;

public sealed partial record PiSharpExtensionContext
{
    /// <summary>The active session projection and durable extension-owned state capability.</summary>
    public PiSharpExtensionSessionContext? Session { get; init; }
}

public sealed partial class PiSharpExtensionCommandContext : IDisposable
{
    /// <summary>The active session projection and durable extension-owned state capability.</summary>
    public PiSharpExtensionSessionContext? Session { get; init; }

    /// <summary>Ends the command callback scope and revokes later session mutations.</summary>
    public void Dispose() => Session?.Dispose();
}
