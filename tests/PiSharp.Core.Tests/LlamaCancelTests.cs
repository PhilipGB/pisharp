using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Core.Models;
using PiSharp.Core.Models.Providers;

namespace PiSharp.Core.Tests;

/// <summary>
/// Item 9: /llama load and download cancellation. The first Ctrl+C (the pinned TUI Esc)
/// stops the operation on the server (client.unload), restores the models a replaced load
/// displaced — with a fresh token, since the operation's token is already cancelled — and
/// returns to the menu. An app-shutdown cancellation propagates instead (the process is
/// exiting; nothing is restored).
/// </summary>
public sealed class LlamaCancelTests
{
    [Fact]
    public async Task LoadCancelled_StopsServerSideLoadAndRestoresReplacedModels()
    {
        await using var router = new ScriptedLlamaRouter();
        router.SetStatus("replaced", LlamaModelStatusValues.Loaded);
        router.SetStatus("target", LlamaModelStatusValues.Loading);
        router.StuckLoading("target"); // the target never finishes loading
        var console = new FakeConsoleIO();
        console.EnqueueText("1\n"); // "Unload all and load"
        var (runtime, _, _) = await ModelRuntimeTestKit.CreateAsync();
        var client = new LlamaClient(router.BaseUrl);
        var catalog = await client.ListAsync();
        var target = catalog.Single(model => model.Id == "target");
        using var shutdown = new CancellationTokenSource();

        var operation = LlamaCommands.LoadModelAsync(console, runtime, client, catalog, target, shutdown.Token);
        await UntilAsync(() => router.LoadCalls.Contains("target")); // load in flight
        Assert.True(LlamaCommands.TryCancelActiveOperation()); // first Ctrl+C
        await operation; // returns to the menu, no throw

        // The replaced model was unloaded up front and restored after the cancel (the
        // restore must run on a fresh token — the operation token is already cancelled).
        Assert.Contains("replaced", router.UnloadCalls);
        Assert.Equal(["target", "replaced"], router.LoadCalls);
        // The in-flight load was stopped on the server (pinned cancel callback).
        Assert.Contains("target", router.UnloadCalls);
    }

    [Fact]
    public async Task LoadCancelledByAppShutdown_PropagatesWithoutRestore()
    {
        await using var router = new ScriptedLlamaRouter();
        router.SetStatus("replaced", LlamaModelStatusValues.Loaded);
        router.SetStatus("target", LlamaModelStatusValues.Loading);
        router.StuckLoading("target");
        var console = new FakeConsoleIO();
        console.EnqueueText("1\n");
        var (runtime, _, _) = await ModelRuntimeTestKit.CreateAsync();
        var client = new LlamaClient(router.BaseUrl);
        var catalog = await client.ListAsync();
        var target = catalog.Single(model => model.Id == "target");
        using var shutdown = new CancellationTokenSource();

        var operation = LlamaCommands.LoadModelAsync(console, runtime, client, catalog, target, shutdown.Token);
        await UntilAsync(() => router.LoadCalls.Contains("target"));
        shutdown.Cancel(); // app shutdown (second Ctrl+C), not the operation

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
        // No server-side stop and no restore: the process is going away.
        Assert.DoesNotContain("target", router.UnloadCalls);
        Assert.DoesNotContain("replaced", router.LoadCalls);
    }

    [Fact]
    public async Task DownloadCancelled_StopsServerSideDownload()
    {
        await using var router = new ScriptedLlamaRouter();
        router.SetStatus("repo:Q4_K_M", LlamaModelStatusValues.Downloading);
        var (runtime, _, _) = await ModelRuntimeTestKit.CreateAsync();
        var client = new LlamaClient(router.BaseUrl);
        using var shutdown = new CancellationTokenSource();

        var operation = LlamaCommands.DownloadModelCoreAsync(runtime, client, "repo:Q4_K_M", shutdown.Token);
        await UntilAsync(() => router.DownloadCalls.Contains("repo:Q4_K_M"));
        Assert.True(LlamaCommands.TryCancelActiveOperation()); // first Ctrl+C
        await operation; // back to the menu, no throw

        // Pinned downloadModel cancel: stop the server-side download; nothing restored.
        Assert.Equal(["repo:Q4_K_M"], router.UnloadCalls);
        Assert.Empty(router.LoadCalls);
    }

    [Fact]
    public async Task TryCancelActiveOperation_FalseWhenIdleOrAlreadyCancelled()
    {
        Assert.False(LlamaCommands.TryCancelActiveOperation()); // idle

        await using var router = new ScriptedLlamaRouter();
        router.SetStatus("repo:Q4_K_M", LlamaModelStatusValues.Downloading);
        var (runtime, _, _) = await ModelRuntimeTestKit.CreateAsync();
        var client = new LlamaClient(router.BaseUrl);
        using var shutdown = new CancellationTokenSource();

        var operation = LlamaCommands.DownloadModelCoreAsync(runtime, client, "repo:Q4_K_M", shutdown.Token);
        await UntilAsync(() => router.DownloadCalls.Contains("repo:Q4_K_M"));
        Assert.True(LlamaCommands.TryCancelActiveOperation()); // first press
        Assert.False(LlamaCommands.TryCancelActiveOperation()); // second press would exit the app
        await operation;
    }

    /// <summary>Polls until the condition holds (or times out), without blocking a thread.</summary>
    private static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("condition was not met within the timeout");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// In-process llama.cpp router stand-in with mutable per-model statuses and a record of
    /// every load/unload/download POST. Models marked stuck stay "loading" forever.
    /// </summary>
    private sealed class ScriptedLlamaRouter : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly Dictionary<string, string> _statuses = new(StringComparer.Ordinal);
        private readonly HashSet<string> _stuckLoading = new(StringComparer.Ordinal);
        private readonly List<string> _loadCalls = [];
        private readonly List<string> _unloadCalls = [];
        private readonly List<string> _downloadCalls = [];

        public string BaseUrl { get; }

        public IReadOnlyList<string> LoadCalls
        {
            get { lock (_loadCalls) { return _loadCalls.ToArray(); } }
        }

        public IReadOnlyList<string> UnloadCalls
        {
            get { lock (_unloadCalls) { return _unloadCalls.ToArray(); } }
        }

        public IReadOnlyList<string> DownloadCalls
        {
            get { lock (_downloadCalls) { return _downloadCalls.ToArray(); } }
        }

        public ScriptedLlamaRouter()
        {
            var port = GetFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public void SetStatus(string id, string status)
        {
            lock (_statuses)
            {
                _statuses[id] = status;
            }
        }

        public void StuckLoading(string id)
        {
            lock (_stuckLoading)
            {
                _stuckLoading.Add(id);
            }
        }

        private string CatalogJson()
        {
            lock (_statuses)
            {
                var entries = _statuses
                    .Select(pair =>
                        $"{{\"id\":{JsonSerializer.Serialize(pair.Key)},\"status\":{{\"value\":{JsonSerializer.Serialize(pair.Value)}}}}}");
                return "[" + string.Join(",", entries) + "]";
            }
        }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return; // listener stopped
                }

                var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                string body = string.Empty;
                if (context.Request.HasEntityBody)
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    body = await reader.ReadToEndAsync();
                }

                var model = ModelFromBody(body);
                string response = "{}";
                if (path is "/models" or "/models/")
                {
                    response = context.Request.HttpMethod == "POST"
                        ? HandleDownload(model)
                        : $"{{\"data\":{CatalogJson()}}}";
                }
                else if (path == "/models/load")
                {
                    HandleLoad(model);
                }
                else if (path == "/models/unload")
                {
                    HandleUnload(model);
                }

                var buffer = Encoding.UTF8.GetBytes(response);
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(buffer);
                context.Response.Close();
            }
        }

        private string HandleLoad(string? model)
        {
            if (model is not null)
            {
                lock (_loadCalls)
                {
                    _loadCalls.Add(model);
                }

                lock (_statuses)
                {
                    _statuses[model] = _stuckLoading.Contains(model) ? LlamaModelStatusValues.Loading : LlamaModelStatusValues.Loaded;
                }
            }

            return "{}";
        }

        private string HandleUnload(string? model)
        {
            if (model is not null)
            {
                lock (_unloadCalls)
                {
                    _unloadCalls.Add(model);
                }

                lock (_statuses)
                {
                    _statuses[model] = LlamaModelStatusValues.Unloaded;
                }
            }

            return "{}";
        }

        private string HandleDownload(string? model)
        {
            if (model is not null)
            {
                lock (_downloadCalls)
                {
                    _downloadCalls.Add(model);
                }
            }

            return "{}";
        }

        private static string? ModelFromBody(string body)
        {
            if (body.Length == 0)
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                ? model.GetString()
                : null;
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _listener.Stop();
            }
            catch (HttpListenerException)
            {
                // Listener already stopped.
            }

            try
            {
                await _loop;
            }
            catch
            {
                // Serve loop ends when the listener stops.
            }
        }
    }
}
