namespace PiSharp.Runtime.Mcp;

/// <summary>Retains the challenged token across SDK discovery and a shared token grant.</summary>
internal static class McpOAuthRequestContext
{
    private sealed class RequestState
    {
        public string? RejectedToken;
    }

    private static readonly AsyncLocal<RequestState?> s_current = new();

    internal static string? RejectedToken => s_current.Value?.RejectedToken;

    internal static void RecordRejectedToken(string? token)
    {
        if (s_current.Value is { } state) state.RejectedToken = token;
    }

    internal static IDisposable Begin()
    {
        var previous = s_current.Value;
        s_current.Value = new RequestState();
        return new Scope(previous);
    }

    private sealed class Scope(RequestState? previous) : IDisposable
    {
        public void Dispose() => s_current.Value = previous;
    }
}
