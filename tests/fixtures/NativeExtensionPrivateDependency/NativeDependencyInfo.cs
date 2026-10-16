namespace PiSharp.TestExtensions.NativePrivateDependency;

public static class NativeDependencyInfo
{
    public static string Version => typeof(NativeDependencyInfo).Assembly.GetName().Version?.ToString() ?? "unknown";
}
