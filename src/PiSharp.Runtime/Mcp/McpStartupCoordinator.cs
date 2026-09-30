using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Mcp;

/// <summary>Owns startup tasks and waits only when the active operation needs an MCP server.</summary>
internal sealed class McpStartupCoordinator : IAsyncDisposable
{
    private sealed record Startup(McpServerConfiguration Configuration, Task<string?> Task);

    private readonly object _gate = new();
    private readonly List<Startup> _servers = [];
    private readonly CancellationTokenSource _lifetime;

    public McpStartupCoordinator(CancellationToken cancellationToken) =>
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    public CancellationToken CancellationToken => _lifetime.Token;

    public void Add(McpServerConfiguration configuration, Task<string?> task)
    {
        lock (_gate) _servers.Add(new(configuration, task));
    }

    public async Task<(IReadOnlyList<string> Errors, bool TimedOut, IReadOnlyList<string> PendingServers)> WaitForServersAsync(
        TimeSpan maximumWait, CancellationToken cancellationToken, bool directOnly)
    {
        var waiting = Snapshot().Where(item => item.Configuration.Enabled &&
            (!directOnly || HasDirectTools(item.Configuration))).ToArray();
        if (waiting.Length == 0) return ([], false, []);
        var all = Task.WhenAll(waiting.Select(item => item.Task));
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(maximumWait, delayCancellation.Token);
        var completed = await Task.WhenAny(all, delay).ConfigureAwait(false);
        if (completed == delay)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (ReadErrors(waiting.Where(item => item.Task.IsCompletedSuccessfully)), true,
                waiting.Where(item => !item.Task.IsCompleted).Select(item => item.Configuration.Name).ToArray());
        }
        delayCancellation.Cancel();
        return (ReadErrors(waiting), false, []);
    }

    public async ValueTask WaitForToolCallAsync(PiSharpToolCallContext call, CancellationToken cancellationToken)
    {
        var waiting = Snapshot().Where(item => item.Configuration.Enabled && !item.Task.IsCompleted).ToArray();
        if (waiting.Length == 0) return;

        IEnumerable<Startup> required = call.ToolName switch
        {
            "codemode" => waiting.Where(item => ScriptNeedsServer(ReadCode(call), item.Configuration.Name)),
            "tool_search" or "list_mcp_resources" or "list_mcp_resource_templates" or "read_mcp_resource" => waiting,
            _ => []
        };
        var tasks = required.Select(item => item.Task).ToArray();
        if (tasks.Length == 0) return;
        try { await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private Startup[] Snapshot()
    {
        lock (_gate) return _servers.ToArray();
    }

    private static IReadOnlyList<string> ReadErrors(IEnumerable<Startup> completed) => completed
        .Where(item => item.Task.IsCompletedSuccessfully)
        .Select(item => item.Task.Result)
        .OfType<string>()
        .ToArray();

    private static bool HasDirectTools(McpServerConfiguration server) =>
        server.Exposure == McpToolExposure.Direct || server.ToolExposure.Values.Contains(McpToolExposure.Direct);

    private static string ReadCode(PiSharpToolCallContext call) =>
        call.Arguments.TryGetValue("code", out var value) && value is string code ? code : "";

    private static bool ScriptNeedsServer(string code, string name)
    {
        if (code.Contains("searchTools", StringComparison.Ordinal) ||
            code.Contains("describeNamespace", StringComparison.Ordinal) ||
            code.Contains("describeTool", StringComparison.Ordinal) ||
            code.Contains("ALL_TOOLS", StringComparison.Ordinal)) return true;
        var namespaceName = "mcp__" + name;
        return code.Contains(namespaceName, StringComparison.Ordinal) ||
            code.Contains(CodemodeToolCatalog.JavascriptIdentifier(namespaceName), StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        var tasks = Snapshot().Select(item => item.Task).ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            _lifetime.Dispose();
        }
    }
}
