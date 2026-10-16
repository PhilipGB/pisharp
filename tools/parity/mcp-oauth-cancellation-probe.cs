using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Authentication;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;
using PiSharp.Tests;

var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-race-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
await using var server = new McpOAuthRecoveryServer();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
using var canceled = new CancellationTokenSource();
using var timedOut = new CancellationTokenSource();
try
{
    await new McpTokenCache(root).ForServer("protected", server.Endpoint).StoreTokensAsync(new TokenContainer
    {
        AccessToken = "old", RefreshToken = "refresh-old", TokenType = "Bearer", ObtainedAt = DateTimeOffset.UtcNow,
        ExpiresIn = 3600, ClientId = "fixture-client", AuthorizationServer = server.Origin, TokenEndpointAuthMethod = "none"
    }, deadline.Token);
    using var configuration = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        url = server.Endpoint.AbsoluteUri, exposure = "direct", oauth = new { clientId = "fixture-client" }
    }));
    var entry = McpConfiguration.Parse("protected", configuration.RootElement, "mcp.json", "global");
    using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);
    try
    {
        var errors = await McpRuntime.RegisterAsync(new McpConfiguration([entry], false, []) { AgentDirectory = root },
            catalog, root, deadline.Token);
        if (errors.Count != 0) throw new InvalidOperationException(string.Join('\n', errors));
        var echo = catalog.Registration.ToolDefinitions.Single(tool => tool.Function.Name == "mcp__protected__echo").Function;
        Task<object?> Call(string value, CancellationToken token) => echo.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?> { ["value"] = value }), token).AsTask();
        var aborted = Call("aborted", canceled.Token);
        await server.RefreshStarted.Task.WaitAsync(deadline.Token);
        var expired = Call("timed-out", timedOut.Token);
        var kept = Call("kept", deadline.Token);
        await server.AllUnauthorized.Task.WaitAsync(deadline.Token);
        canceled.Cancel();
        timedOut.CancelAfter(TimeSpan.FromMilliseconds(100));
        async Task<bool> CanceledBeforeRelease(Task<object?> task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)); return false; }
            catch (OperationCanceledException) { return true; }
        }
        var settledBeforeRelease = new[] { await CanceledBeforeRelease(aborted), await CanceledBeforeRelease(expired) };
        if (server.Executed.Length != 0) throw new InvalidOperationException("Call executed before refresh release.");
        server.ReleaseRefresh.TrySetResult();
        var keptText = ((PiSharpToolResult)(await kept.WaitAsync(deadline.Token))!).Text;
        var cached = await new McpTokenCache(root).ForServer("protected", server.Endpoint).GetTokensAsync(deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            settledBeforeRelease, executed = server.Executed, refreshes = server.Refreshes, keptText,
            storedAccess = cached?.AccessToken, storedRefresh = cached?.RefreshToken,
            bindingPreserved = cached?.ClientId == "fixture-client" && cached.AuthorizationServer == server.Origin
        }));
    }
    finally { server.ReleaseRefresh.TrySetResult(); }
}
finally { Directory.Delete(root, true); }
