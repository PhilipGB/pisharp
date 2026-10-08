using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class LlamaRouterManagerTuiTests
{
    [Fact]
    public async Task LlamaManagerKeepsOrUnloadsOtherModelsOnlyAfterTheUserChooses()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-manager-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await using var server = new RouterServer();
        await using var terminal = new ManagerTerminal(root, agent, server.Origin);
        try
        {
            var initialEditor = await terminal.WaitEditorAsync();
            Assert.Contains("fixture-model", initialEditor);
            Assert.DoesNotContain("(fixture) fixture-model", initialEditor);
            var mark = terminal.Mark;
            await terminal.SendAsync("/llama\n");
            await terminal.WaitManagerAsync(mark);
            await terminal.SendAsync("\u001b[B\n");
            await terminal.WaitTextAsync("1 model is loaded");
            mark = terminal.Mark;
            await terminal.SendAsync("\u001b[B\n");
            await terminal.WaitTextAsync("Loading model", mark);
            await terminal.WaitTextAsync("Loaded target", mark);
            Assert.Equal("loaded", server.Statuses["target"]);

            mark = terminal.Mark;
            await terminal.WaitManagerAsync(mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Unload model?", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Unloaded qwen", mark);
            mark = terminal.Mark;
            await terminal.WaitManagerAsync(mark);
            Assert.Equal("unloaded", server.Statuses["qwen"]);
            Assert.Contains(server.Requests, request => request.Method == "POST" && request.Path == "/models/load" &&
                request.Body == "{\"model\":\"target\"}");
            Assert.Contains(server.Requests, request => request.Method == "POST" && request.Path == "/models/unload" &&
                request.Body == "{\"model\":\"qwen\"}");
            Assert.All(server.Requests, request => Assert.Equal("Bearer local", request.Authorization));

            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            var restoredEditor = await terminal.WaitEditorAsync(mark);
            Assert.DoesNotContain("(fixture) fixture-model", restoredEditor);
            await terminal.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CancellingALoadConfirmsAndUnloadsTheInFlightModel()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-cancel-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await using var server = new RouterServer(holdLoad: true);
        await using var terminal = new ManagerTerminal(root, agent, server.Origin);
        try
        {
            await terminal.WaitTextAsync("fixture-model");
            var mark = terminal.Mark;
            await terminal.SendAsync("/llama\n");
            await terminal.WaitManagerAsync(mark);
            await terminal.SendAsync("\u001b[B\n");
            await terminal.WaitTextAsync("1 model is loaded");
            mark = terminal.Mark;
            await terminal.SendAsync("\u001b[B\n");
            await terminal.WaitTextAsync("25%", mark);
            await server.LoadRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));

            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            await terminal.WaitTextAsync("Stop loading?", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitManagerAsync(mark);

            Assert.Equal("unloaded", server.Statuses["target"]);
            Assert.Contains(server.Requests, request => request.Method == "POST" && request.Path == "/models/unload" &&
                request.Body == "{\"model\":\"target\"}");
            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            await terminal.WaitEditorAsync(mark);
            await terminal.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CancellingAReplacementLoadRestoresPreviouslyLoadedModels()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-replace-cancel-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await using var server = new RouterServer(holdLoad: true, holdRestore: true);
        await using var terminal = new ManagerTerminal(root, agent, server.Origin);
        try
        {
            await terminal.WaitTextAsync("fixture-model");
            var mark = terminal.Mark;
            await terminal.SendAsync("/llama\n");
            await terminal.WaitManagerAsync(mark);
            await terminal.SendAsync("\u001b[B\n");
            await terminal.WaitTextAsync("1 model is loaded");

            mark = terminal.Mark;
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("25%", mark);
            await server.LoadRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("unloaded", server.Statuses["qwen"]);

            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            await terminal.WaitTextAsync("Stop loading?", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Restoring previously loaded models", mark);
            await server.RestoreLoadRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var restoring = await terminal.WaitFrameAsync(frame =>
                frame.Contains("Restoring previously loaded models", StringComparison.Ordinal) &&
                frame.Contains("Stop loading?", StringComparison.Ordinal), mark);
            Assert.Contains("Yes", restoring, StringComparison.Ordinal);
            server.ReleaseRestore();
            await terminal.WaitManagerAsync(mark);

            Assert.Equal("unloaded", server.Statuses["target"]);
            Assert.Equal("loaded", server.Statuses["qwen"]);
            var mutations = server.Requests.Where(request => request.Method == "POST" &&
                request.Path is "/models/load" or "/models/unload").ToArray();
            Assert.Equal(
                [
                    ("/models/unload", "{\"model\":\"qwen\"}"),
                    ("/models/load", "{\"model\":\"target\"}"),
                    ("/models/unload", "{\"model\":\"target\"}"),
                    ("/models/load", "{\"model\":\"qwen\"}")
                ],
                mutations.Select(request => (request.Path, request.Body)));
            Assert.All(server.Requests, request => Assert.Equal("Bearer local", request.Authorization));

            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            await terminal.WaitEditorAsync(mark);
            await terminal.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DownloadSearchGatedApprovalAndQuantizationReachTheRouter()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-download-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await using var router = new RouterServer();
        await using var huggingFace = new HuggingFaceServer();
        await using var terminal = new ManagerTerminal(root, agent, router.Origin, huggingFace.Origin, "hf-fixture-token");
        try
        {
            await terminal.WaitTextAsync("fixture-model");
            var mark = terminal.Mark;
            await terminal.SendAsync("/llama\n");
            await terminal.WaitManagerAsync(mark);
            await terminal.SendAsync("\u001b[B\u001b[B\n");
            await terminal.WaitTextAsync("Model name or owner/repository[:quant]", mark);
            await terminal.SendAsync("model\n");
            await terminal.WaitTextAsync("owner/model", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Hugging Face access required", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Select quantization", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Downloaded owner/model:Q4_K_M", mark);

            Assert.Contains(huggingFace.Requests, request => request.Path.Contains("search=model&", StringComparison.Ordinal) &&
                request.Path.Contains("filter=gguf", StringComparison.Ordinal) && request.Authorization == "Bearer hf-fixture-token");
            Assert.Contains(huggingFace.Requests, request => request.Path == "/api/models/owner/model?blobs=true" &&
                request.Authorization == "Bearer hf-fixture-token");
            Assert.Contains(router.Requests, request => request.Method == "POST" && request.Path == "/models" &&
                request.Body == "{\"model\":\"owner/model:Q4_K_M\"}");
            Assert.Equal("loaded", router.Statuses["owner/model:Q4_K_M"]);
            Assert.DoesNotContain("hf-fixture-token", terminal.Output);

            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            await terminal.WaitEditorAsync(mark);
            await terminal.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CancellingADownloadShowsRouterProgressAndUnloadsTheInFlightModel()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-cancel-download-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await using var router = new RouterServer(holdDownload: true);
        await using var huggingFace = new HuggingFaceServer();
        await using var terminal = new ManagerTerminal(root, agent, router.Origin, huggingFace.Origin, "hf-fixture-token");
        try
        {
            await terminal.WaitTextAsync("fixture-model");
            var mark = terminal.Mark;
            await terminal.SendAsync("/llama\n");
            await terminal.WaitManagerAsync(mark);
            await terminal.SendAsync("\u001b[B\u001b[B\n");
            await terminal.WaitTextAsync("Model name or owner/repository[:quant]", mark);
            await terminal.SendAsync("model\n");
            await terminal.WaitTextAsync("owner/model", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Hugging Face access required", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Select quantization", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("50%", mark);
            await router.DownloadRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));

            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            await terminal.WaitTextAsync("Stop download?", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitManagerAsync(mark);

            Assert.Equal("unloaded", router.Statuses["owner/model:Q4_K_M"]);
            Assert.DoesNotContain(router.Requests, request => request.Method == "GET" && request.Path == "/models?reload=1");
            Assert.Contains(router.Requests, request => request.Method == "POST" && request.Path == "/models/unload" &&
                request.Body == "{\"model\":\"owner/model:Q4_K_M\"}");
            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            await terminal.WaitEditorAsync(mark);
            await terminal.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SleepingRouterModelsCanBeUnloadedFromTheManager()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-sleeping-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await using var server = new RouterServer(sleepingQwen: true);
        await using var terminal = new ManagerTerminal(root, agent, server.Origin);
        try
        {
            await terminal.WaitTextAsync("fixture-model");
            var mark = terminal.Mark;
            await terminal.SendAsync("/llama\n");
            await terminal.WaitManagerAsync(mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Unload model?", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Unloaded qwen", mark);
            mark = terminal.Mark;
            await terminal.WaitManagerAsync(mark);
            Assert.Equal("unloaded", server.Statuses["qwen"]);
            Assert.Contains(server.Requests, request => request.Method == "POST" && request.Path == "/models/unload" &&
                request.Body == "{\"model\":\"qwen\"}");
            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            await terminal.WaitEditorAsync(mark);
            await terminal.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NewlyLoadedRouterModelIsSelectableWithModelCommand()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-model-picker-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await using var server = new RouterServer();
        await using var terminal = new ManagerTerminal(root, agent, server.Origin);
        try
        {
            await terminal.WaitTextAsync("fixture-model");
            var mark = terminal.Mark;
            await terminal.SendAsync("/llama\n");
            await terminal.WaitManagerAsync(mark);
            await terminal.SendAsync("\u001b[B\n");
            await terminal.WaitTextAsync("1 model is loaded");
            await terminal.SendAsync("\u001b[B\n");
            mark = terminal.Mark;
            await terminal.WaitTextAsync("Loaded target", mark);
            mark = terminal.Mark;
            await terminal.WaitManagerAsync(mark);
            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            await terminal.WaitEditorAsync(mark);

            mark = terminal.Mark;
            await terminal.SendAsync("/model\n");
            await terminal.WaitTextAsync("type to filter", mark);
            await terminal.SendAsync("target");
            await terminal.WaitTextAsync("target [llama.cpp]", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Model: llama.cpp/target", mark);
            await terminal.WaitTextAsync("Model: llama.cpp/target", mark);
            Assert.Equal("loaded", server.Statuses["target"]);
            await terminal.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RouterConnectionScreenCanRetryCatalogDiscovery()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-retry-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await using var server = new RouterServer(failFirstCatalog: true);
        await using var terminal = new ManagerTerminal(root, agent, server.Origin);
        try
        {
            await terminal.WaitTextAsync("fixture-model");
            var mark = terminal.Mark;
            await terminal.SendAsync("/llama\n");
            await terminal.WaitTextAsync("llama.cpp unavailable", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitManagerAsync(mark);
            Assert.Equal(3, server.CatalogRequests);
            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            await terminal.WaitEditorAsync(mark);
            await terminal.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RouterServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serve;
        private readonly bool _holdLoad;
        private readonly bool _holdDownload;
        private readonly bool _holdRestore;
        private readonly bool _failFirstCatalog;
        private readonly ConcurrentBag<Task> _background = [];
        private readonly TaskCompletionSource _downloadRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _catalogRequests;
        public ConcurrentQueue<(string Method, string Path, string? Authorization, string? Body)> Requests { get; } = new();
        public TaskCompletionSource LoadRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RestoreLoadRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _restoreLoadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DownloadRequested => _downloadRequested;
        public int CatalogRequests => Volatile.Read(ref _catalogRequests);
        public Dictionary<string, string> Statuses { get; } = new(StringComparer.Ordinal)
        {
            ["qwen"] = "loaded",
            ["target"] = "unloaded"
        };
        public string Origin { get; }

        public RouterServer(bool holdLoad = false, bool failFirstCatalog = false, bool holdDownload = false,
            bool sleepingQwen = false, bool holdRestore = false)
        {
            _holdLoad = holdLoad;
            _failFirstCatalog = failFirstCatalog;
            _holdDownload = holdDownload;
            _holdRestore = holdRestore;
            if (sleepingQwen) Statuses["qwen"] = "sleeping";
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            Origin = "http://127.0.0.1:" + ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _serve = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
                    string? body = null;
                    if (context.Request.HasEntityBody)
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                        body = await reader.ReadToEndAsync(_shutdown.Token);
                    }
                    var path = context.Request.Url!.PathAndQuery;
                    Requests.Enqueue((context.Request.HttpMethod, path, context.Request.Headers["Authorization"], body));
                    string response;
                    string contentType = "application/json";
                    var announceDownload = false;
                    if (context.Request.HttpMethod == "POST" && path == "/models/load")
                    {
                        using var payload = JsonDocument.Parse(body!);
                        var model = payload.RootElement.GetProperty("model").GetString()!;
                        var holdRestore = _holdRestore && model == "qwen";
                        Statuses[model] = _holdLoad && model == "target" ? "loading" : "loaded";
                        LoadRequested.TrySetResult();
                        if (holdRestore)
                        {
                            RestoreLoadRequested.TrySetResult();
                            await _restoreLoadRelease.Task.WaitAsync(_shutdown.Token);
                        }
                        response = "{}";
                    }
                    else if (context.Request.HttpMethod == "POST" && path == "/models/unload")
                    {
                        using var payload = JsonDocument.Parse(body!);
                        Statuses[payload.RootElement.GetProperty("model").GetString()!] = "unloaded";
                        response = "{}";
                    }
                    else if (context.Request.HttpMethod == "POST" && path == "/models")
                    {
                        using var payload = JsonDocument.Parse(body!);
                        Statuses[payload.RootElement.GetProperty("model").GetString()!] = _holdDownload ? "downloading" : "loaded";
                        announceDownload = _holdDownload;
                        response = "{}";
                    }
                    else if (context.Request.HttpMethod == "GET" && path is "/models" or "/models?reload=1")
                    {
                        var requestNumber = Interlocked.Increment(ref _catalogRequests);
                        if (_failFirstCatalog && requestNumber == 1)
                        {
                            context.Response.StatusCode = 503;
                            response = "{\"error\":{\"message\":\"temporary router failure\"}}";
                        }
                        else
                        {
                            var values = Statuses.Select(item => new
                            {
                                id = item.Key,
                                status = new { value = item.Value },
                                source = "local",
                                architecture = new { output_modalities = new[] { "text" } },
                                meta = new { n_ctx = 4096 }
                            });
                            response = JsonSerializer.Serialize(new { data = values });
                        }
                    }
                    else if (context.Request.HttpMethod == "GET" && path == "/models/sse")
                    {
                        contentType = "text/event-stream";
                        if (_holdDownload)
                        {
                            _background.Add(RespondDownloadProgressAsync(context));
                            continue;
                        }
                        response = _holdLoad
                            ? "data: {\"model\":\"target\",\"event\":\"model_status\",\"data\":{\"status\":\"loading\",\"progress\":{\"current\":\"loading_model\",\"stages\":[\"loading_model\",\"loading_context\"],\"value\":0.5}}}\n\n"
                            : "data: {\"model\":\"owner/model:Q4_K_M\",\"event\":\"download_finished\",\"data\":{}}\n\n";
                    }
                    else if (context.Request.HttpMethod == "GET" && path.StartsWith("/props", StringComparison.Ordinal))
                        response = "{}";
                    else
                    {
                        context.Response.StatusCode = 404;
                        response = "{}";
                    }
                    var bytes = Encoding.UTF8.GetBytes(response);
                    context.Response.ContentType = contentType;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, _shutdown.Token);
                    context.Response.Close();
                    if (announceDownload) _downloadRequested.TrySetResult();
                }
            }
            catch (Exception error) when (_shutdown.IsCancellationRequested &&
                error is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException)
            { }
        }

        private async Task RespondDownloadProgressAsync(HttpListenerContext context)
        {
            try
            {
                await _downloadRequested.Task.WaitAsync(_shutdown.Token);
                var response = "data: {\"model\":\"owner/model:Q4_K_M\",\"event\":\"download_progress\",\"data\":{\"progress\":{\"model.gguf\":{\"done\":50,\"total\":100}}}}\n\n";
                var bytes = Encoding.UTF8.GetBytes(response);
                context.Response.ContentType = "text/event-stream";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, _shutdown.Token);
                context.Response.Close();
            }
            catch (Exception error) when (_shutdown.IsCancellationRequested &&
                error is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException)
            { }
        }

        public async ValueTask DisposeAsync()
        {
            _restoreLoadRelease.TrySetResult();
            _shutdown.Cancel();
            _listener.Close();
            await _serve;
            await Task.WhenAll(_background);
            _shutdown.Dispose();
        }

        public void ReleaseRestore() => _restoreLoadRelease.TrySetResult();
    }

    private sealed class HuggingFaceServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serve;
        public ConcurrentQueue<(string Path, string? Authorization)> Requests { get; } = new();
        public string Origin { get; }

        public HuggingFaceServer()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            Origin = "http://127.0.0.1:" + ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _serve = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
                    var path = context.Request.Url!.PathAndQuery;
                    Requests.Enqueue((path, context.Request.Headers["Authorization"]));
                    var response = path.StartsWith("/api/models?", StringComparison.Ordinal)
                        ? "[{\"id\":\"owner/model\",\"downloads\":2500}]"
                        : path == "/api/models/owner/model?blobs=true"
                            ? "{\"id\":\"owner/model\",\"gated\":\"auto\",\"siblings\":[{\"rfilename\":\"model-Q4_K_M.gguf\",\"size\":1000}]}"
                            : "{}";
                    var bytes = Encoding.UTF8.GetBytes(response);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, _shutdown.Token);
                    context.Response.Close();
                }
            }
            catch (Exception error) when (_shutdown.IsCancellationRequested &&
                error is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException)
            { }
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Close();
            await _serve;
            _shutdown.Dispose();
        }
    }

    private sealed class ManagerTerminal : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly object _gate = new();
        private TaskCompletionSource _changed = NewSignal();
        private readonly Task _readOutput;
        private readonly Task<string> _readError;
        private readonly string _editorModelText;
        private bool _closed;
        public string Output { get { lock (_gate) return _output.ToString(); } }
        public int Mark { get { lock (_gate) return _output.Length; } }

        public ManagerTerminal(string root, string agent, string serverUrl, string? hfEndpoint = null,
            string? hfToken = null, bool llamaProvider = false)
        {
            _editorModelText = llamaProvider ? "llama.cpp/qwen" : "fixture-model";
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("-c");
            var arguments = llamaProvider
                ? " --provider llama.cpp --model qwen --no-session --no-tools"
                : " --provider fixture --model fixture-model --offline --no-session --no-tools";
            start.ArgumentList.Add("stty rows 30 cols 100; exec dotnet " + ShellQuote(typeof(CliArguments).Assembly.Location) + arguments);
            start.ArgumentList.Add("/dev/null");
            foreach (var name in new[] { "LLAMA_BASE_URL", "LLAMA_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL",
                         "PISHARP_MODEL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["LLAMA_BASE_URL"] = serverUrl;
            if (hfEndpoint is not null) start.Environment["HF_ENDPOINT"] = hfEndpoint;
            if (hfToken is not null) start.Environment["HF_TOKEN"] = hfToken;
            _process = Process.Start(start)!;
            _readOutput = ReadOutputAsync();
            _readError = _process.StandardError.ReadToEndAsync();
        }

        public async Task SendAsync(string input)
        {
            await _process.StandardInput.WriteAsync(input);
            await _process.StandardInput.FlushAsync();
        }

        public Task<string> WaitTextAsync(string value, int after = 0) => WaitAsync(output =>
            StripAnsi(output[after..]).Contains(value, StringComparison.Ordinal) ? output[after..] : null);

        public Task<string> WaitManagerAsync(int after = 0) => WaitFrameAsync(frame =>
            frame.Contains("llama.cpp models", StringComparison.Ordinal) &&
            !frame.Contains("Loading…", StringComparison.Ordinal) &&
            frame.Contains("qwen", StringComparison.Ordinal), after);

        public Task<string> WaitEditorAsync(int after = 0) => WaitFrameAsync(frame =>
            frame.Contains(_editorModelText, StringComparison.Ordinal) &&
            !frame.Contains("llama.cpp models", StringComparison.Ordinal) &&
            !frame.Contains("Unload model?", StringComparison.Ordinal), after);

        public Task<string> WaitFrameAsync(Func<string, bool> predicate, int after = 0) => WaitAsync(output =>
        {
            foreach (var frame in TerminalOutputFrameReader.Read(output, rows: 30, columns: 100))
                if (frame.Start >= after && predicate(frame.Screen)) return frame.Screen;
            return null;
        });

        private async Task<string> WaitAsync(Func<string, string?> match)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (match(_output.ToString()) is { } result) return result;
                    if (_closed) throw new InvalidOperationException("The llama.cpp manager terminal closed early.\n" + _output);
                    changed = _changed.Task;
                }
                try { await changed.WaitAsync(deadline.Token); }
                catch (OperationCanceledException error)
                {
                    var output = Output;
                    throw new TimeoutException("The llama.cpp manager did not reach the expected screen.\n" +
                        output[^Math.Min(output.Length, 6000)..], error);
                }
            }
        }

        private async Task ReadOutputAsync()
        {
            var buffer = new char[4096];
            while (await _process.StandardOutput.ReadAsync(buffer) is var count && count > 0)
            {
                lock (_gate)
                {
                    _output.Append(buffer, 0, count);
                    _changed.TrySetResult();
                    _changed = NewSignal();
                }
            }
            lock (_gate) { _closed = true; _changed.TrySetResult(); }
        }

        public async Task QuitAsync()
        {
            await SendAsync("/quit\n");
            _process.StandardInput.Close();
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException error)
            {
                throw new TimeoutException("The llama.cpp manager did not accept /quit.\n" + Output[^Math.Min(Output.Length, 5000)..], error);
            }
            await _readOutput;
            Assert.Equal(0, _process.ExitCode);
            Assert.Equal("", await _readError);
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        private static string StripAnsi(string text) => Regex.Replace(text, "\u001b\\[[0-9;?]*[A-Za-z]", "");

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            await _readOutput;
            await _readError;
            _process.Dispose();
        }
    }
}
