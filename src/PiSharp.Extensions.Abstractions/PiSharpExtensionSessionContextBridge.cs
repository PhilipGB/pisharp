namespace PiSharp.Extensions.Abstractions;

public sealed partial record PiSharpExtensionContext : IDisposable
{
    /// <summary>The active session projection and durable extension-owned state capability.</summary>
    public PiSharpExtensionSessionContext? Session { get; init; }

    /// <summary>Safe callback-scoped host state and operations shared with lifecycle callbacks.</summary>
    public PiSharpExtensionCapabilities? Capabilities { get; init; }

    /// <summary>Ends the lifecycle callback scope and revokes retained live operations.</summary>
    public void Dispose()
    {
        Capabilities?.Dispose();
        Session?.Dispose();
    }
}

public sealed partial class PiSharpExtensionCommandContext : IDisposable
{
    /// <summary>The active session projection and durable extension-owned state capability.</summary>
    public PiSharpExtensionSessionContext? Session { get; init; }

    /// <summary>Safe callback-scoped host state and operations shared with lifecycle callbacks.</summary>
    public PiSharpExtensionCapabilities? Capabilities { get; init; }

    /// <summary>Ends the command callback scope and revokes later session mutations.</summary>
    public void Dispose()
    {
        DisposeControlContext();
        Capabilities?.Dispose();
        Session?.Dispose();
        SettingsAndFlags?.Dispose();
    }
}
