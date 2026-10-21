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
    private static readonly object s_routerEndpointGate = new();

    private static string EditorInputLine(string frame)
    {
        return frame.Split('\n').Select(line => line.Trim())
            .LastOrDefault(line => line.StartsWith('@') ||
                line.StartsWith('/') && line.Count(character => character == '/') == 1) ?? "";
    }

    [Fact]
    public async Task ConcurrentRouterFixturesBindDistinctLoopbackPorts()
    {
        var servers = new ConcurrentBag<RouterServer>();
        try
        {
            var started = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            {
                var server = new RouterServer();
                servers.Add(server);
                return server;
            })));

            Assert.Equal(started.Length, started.Select(server => server.Origin).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            await Task.WhenAll(servers.Select(server => server.DisposeAsync().AsTask()));
        }
    }

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
            await terminal.WaitTextAsync("Loaded target", mark);
            Assert.Equal("loaded", server.Statuses["target"]);

            await terminal.WaitManagerAsync(mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Unload model?", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Unloaded qwen", mark);
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
            await terminal.WaitFrameAsync(frame => frame.Contains("Loading model", StringComparison.Ordinal) &&
                !frame.Contains("Starting…", StringComparison.Ordinal), mark);
            await server.LoadRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            server.ReleaseLoadProgress();
            try { await terminal.WaitTextAsync("25%", mark); }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException("The router's released load-progress event was not rendered.", error);
            }

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
            await terminal.WaitTextAsync("Loading model", mark);
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
            mark = terminal.Mark;
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Unload model?", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Unloaded qwen", mark);
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
            mark = terminal.Mark;
            await terminal.SendAsync("\u001b[B\n");
            await terminal.WaitFrameAsync(frame =>
                frame.Contains("llama.cpp models", StringComparison.Ordinal) &&
                frame.Contains("Loaded target", StringComparison.Ordinal) &&
                frame.Split('\n').Any(line => line.Contains("target", StringComparison.Ordinal) &&
                    line.Contains("loaded", StringComparison.Ordinal)), mark);
            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            await terminal.WaitEditorAsync(mark);

            mark = terminal.Mark;
            await terminal.SendAsync("/model\n");
            await terminal.WaitTextAsync("Model Name: fixture-model", mark);
            await terminal.SendAsync("target");
            await terminal.WaitTextAsync("target [llama.cpp]", mark);
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Model: target", mark);
            await terminal.WaitTextAsync("Model: target", mark);
            Assert.Equal("loaded", server.Statuses["target"]);

            mark = terminal.Mark;
            await terminal.SendAsync("/model qwen\n");
            await terminal.WaitTextAsync("Model: qwen", mark);
            Assert.Equal("loaded", server.Statuses["qwen"]);
            await terminal.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ModelPickerCancelAndFailedSwitchPreserveTheWorkingProviderAcrossSuccessfulSwitches()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-model-switch-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var sessions = Path.Combine(root, "sessions");
        var ttyPathFile = Path.Combine(root, "tty-path");
        Directory.CreateDirectory(agent);
        await using var server = new RouterServer(chatResponse: "MODEL_SWITCH_OK");
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), $$$"""
            {"providers":{
              "fixture":{"baseUrl":"{{{server.Origin}}}/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]},
              "blocked":{"baseUrl":"{{{server.Origin}}}/v1","apiKeyEnv":"PISHARP_BLOCKED_KEY","models":[{"id":"blocked-model"}]}
            }}
            """);
        await using var terminal = new ManagerTerminal(root, agent, server.Origin, llamaProvider: true,
            sessionDirectory: sessions, ttyPathFile: ttyPathFile);
        try
        {
            Task<string> WaitForEditorModelAsync(string model, int after = 0, int rows = 30, int columns = 100) => terminal.WaitFrameAsync(frame =>
                frame.Contains(model, StringComparison.Ordinal) &&
                !frame.Contains("Model Name:", StringComparison.Ordinal) &&
                !frame.Contains("Only showing models from configured providers", StringComparison.Ordinal) &&
                !frame.Contains("llama.cpp models", StringComparison.Ordinal), after, rows, columns);
            async Task WaitForTurnAsync(string prompt, int expectedRequestCount, int after)
            {
                using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (server.Requests.Count(item => item.Method == "POST" &&
                    item.Path == "/v1/chat/completions") < expectedRequestCount)
                    await Task.Delay(25, requestTimeout.Token);
                await terminal.WaitFrameAsync(frame => frame.Contains(prompt, StringComparison.Ordinal) &&
                    Regex.Matches(frame, Regex.Escape("MODEL_SWITCH_OK")).Count >= expectedRequestCount &&
                    !frame.Contains("Working", StringComparison.Ordinal), after);
            }
            await WaitForEditorModelAsync("qwen");

            var mark = terminal.Mark;
            await terminal.SendAsync("/model\n");
            await terminal.WaitFrameAsync(frame => frame.Contains("Model Name: qwen", StringComparison.Ordinal) &&
                frame.Contains("fixture-model", StringComparison.Ordinal), mark);
            await ProcessTestHelpers.WaitForFileAsync(ttyPathFile);
            var ttyPath = (await File.ReadAllTextAsync(ttyPathFile)).Trim();
            var resizeMark = terminal.Mark;
            await ResizeLinuxPtyAsync(ttyPath, rows: 18, columns: 60);
            await terminal.WaitFrameAsync(frame => frame.Contains("Model Name: qwen", StringComparison.Ordinal) &&
                frame.Contains("fixture-model", StringComparison.Ordinal), resizeMark, rows: 18, columns: 60);
            var cancelMark = terminal.Mark;
            await terminal.SendRawAsync("\u001b");
            await WaitForEditorModelAsync("qwen", cancelMark, rows: 18, columns: 60);
            var restoreMark = terminal.Mark;
            await ResizeLinuxPtyAsync(ttyPath, rows: 30, columns: 100);
            await terminal.SendRawAsync("\u001b[D\u001b[C");
            await WaitForEditorModelAsync("qwen", restoreMark);

            mark = terminal.Mark;
            await terminal.SendAsync("/model blocked/blocked-model\n");
            await terminal.WaitTextAsync("Provider 'blocked' is not authenticated", mark);
            await WaitForEditorModelAsync("qwen", mark);

            mark = terminal.Mark;
            await terminal.SendAsync("Confirm the active router still works.\n");
            await WaitForTurnAsync("Confirm the active router still works.", 1, mark);

            mark = terminal.Mark;
            await terminal.SendAsync("/model\n");
            await terminal.WaitFrameAsync(frame => frame.Contains("Model Name: qwen", StringComparison.Ordinal) &&
                frame.Contains("fixture-model", StringComparison.Ordinal), mark);
            await terminal.SendAsync("fixture-model");
            await terminal.WaitFrameAsync(frame => frame.Contains("> fixture-model", StringComparison.Ordinal) &&
                frame.Contains("Model Name: fixture-model", StringComparison.Ordinal), mark);
            await terminal.SendRawAsync("\r");
            await terminal.WaitTextAsync("Model: fixture-model", mark);
            await WaitForEditorModelAsync("fixture-model", mark);

            mark = terminal.Mark;
            await terminal.SendAsync("Check the fixture provider.\n");
            await WaitForTurnAsync("Check the fixture provider.", 2, mark);

            mark = terminal.Mark;
            await terminal.SendAsync("/model\n");
            await terminal.WaitFrameAsync(frame => frame.Contains("Model Name: fixture-model", StringComparison.Ordinal) &&
                frame.Contains("qwen", StringComparison.Ordinal), mark);
            await terminal.SendAsync("qwen");
            await terminal.WaitFrameAsync(frame => frame.Contains("> qwen", StringComparison.Ordinal) &&
                frame.Contains("Model Name: qwen", StringComparison.Ordinal), mark);
            await terminal.SendRawAsync("\r");
            await terminal.WaitTextAsync("Model: qwen", mark);
            await WaitForEditorModelAsync("qwen", mark);

            mark = terminal.Mark;
            await terminal.SendAsync("Continue on the router provider.\n");
            await WaitForTurnAsync("Continue on the router provider.", 3, mark);

            var requests = server.Requests.Where(item => item.Method == "POST" &&
                item.Path == "/v1/chat/completions").ToArray();
            Assert.Equal(["qwen", "fixture-model", "qwen"], requests.Select(request =>
                JsonDocument.Parse(request.Body!).RootElement.GetProperty("model").GetString()));
            Assert.Equal(["Bearer local", "Bearer fixture-key", "Bearer local"],
                requests.Select(request => request.Authorization));
            await terminal.QuitAsync(expectedExitCode: 1);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ResumeSwitchesToSavedLlamaModelAndRestoresHistory()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-resume-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await using var server = new RouterServer(chatResponse: "TARGET_MODEL_REPLY");
        server.Statuses["target"] = "loaded";
        var store = new PiSharp.Runtime.Sessions.ConversationStore(root, sessions);
        var saved = new PiSharp.Runtime.Sessions.ConversationSession(root, "target",
            server.Origin + "/v1", "llama.cpp");
        saved.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "prior target context"));
        var savedPath = store.NewPath(saved);
        await store.SaveAsync(saved, savedPath);
        await using var terminal = new ManagerTerminal(root, agent, server.Origin, llamaProvider: true,
            sessionDirectory: sessions);
        try
        {
            await terminal.WaitTextAsync("qwen");
            var mark = terminal.Mark;
            await terminal.SendAsync($"/resume {saved.Id[..12]}\n");
            await terminal.WaitTextAsync($"Resumed {saved.Id[..12]}", mark);

            mark = terminal.Mark;
            await terminal.SendAsync("Continue with the target model.\n");
            await terminal.WaitTextAsync("TARGET_MODEL_REPLY", mark);
            await terminal.WaitFrameAsync(frame => frame.Contains("TARGET_MODEL_REPLY", StringComparison.Ordinal) &&
                !frame.Contains("Working", StringComparison.Ordinal), mark);

            var request = Assert.Single(server.Requests, item => item.Method == "POST" &&
                item.Path == "/v1/chat/completions");
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal("target", body.RootElement.GetProperty("model").GetString());
            Assert.Contains("prior target context", body.RootElement.GetProperty("messages").ToString());
            await terminal.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LocalModelCanUseReadToolAndContinueThroughTheInteractiveRoute()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-tools-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "facts.txt"), "local fixture fact");
        await using var server = new RouterServer(chatResponse: "LOCAL_TOOL_OK", readToolCallFirst: true,
            resumedChatResponse: "LLAMA_RESUMED_OK");
        await using var terminal = new ManagerTerminal(root, agent, server.Origin, llamaProvider: true,
            sessionDirectory: sessions, noTools: false);
        try
        {
            await terminal.WaitTextAsync("qwen");
            var mark = terminal.Mark;
            await terminal.SendAsync("Read facts.txt and answer with its content.\n");
            await terminal.WaitTextAsync("LOCAL_TOOL_OK", mark);
            await terminal.WaitFrameAsync(frame => frame.Contains("LOCAL_TOOL_OK", StringComparison.Ordinal) &&
                !frame.Contains("Working", StringComparison.Ordinal), mark);

            var requests = server.Requests.Where(item => item.Method == "POST" &&
                item.Path == "/v1/chat/completions").ToArray();
            Assert.Equal(2, requests.Length);
            using var firstBody = JsonDocument.Parse(requests[0].Body!);
            Assert.Contains(firstBody.RootElement.GetProperty("tools").EnumerateArray(), tool =>
                tool.GetProperty("function").GetProperty("name").GetString() == "read");
            using var continuationBody = JsonDocument.Parse(requests[1].Body!);
            Assert.Contains("local fixture fact", continuationBody.RootElement.GetProperty("messages").ToString());
            Assert.Contains("read-1", continuationBody.RootElement.GetProperty("messages").ToString());
            Assert.All(requests, request => Assert.Equal("qwen", JsonDocument.Parse(request.Body!).RootElement.GetProperty("model").GetString()));
            await terminal.QuitAsync();

            await using var resumed = new ManagerTerminal(root, agent, server.Origin, llamaProvider: true,
                sessionDirectory: sessions, noTools: false, continueSession: true);
            await resumed.WaitTextAsync("qwen");
            var resumeMark = resumed.Mark;
            await resumed.WaitTextAsync("LOCAL_TOOL_OK", resumeMark);
            resumeMark = resumed.Mark;
            await resumed.SendAsync("Continue from the saved local tool result.\n");
            await resumed.WaitTextAsync("LLAMA_RESUMED_OK", resumeMark);
            await resumed.WaitFrameAsync(frame => frame.Contains("LLAMA_RESUMED_OK", StringComparison.Ordinal) &&
                !frame.Contains("Working", StringComparison.Ordinal), resumeMark);

            var allRequests = server.Requests.Where(item => item.Method == "POST" &&
                item.Path == "/v1/chat/completions").ToArray();
            Assert.Equal(3, allRequests.Length);
            using var resumedBody = JsonDocument.Parse(allRequests[2].Body!);
            var resumedMessages = resumedBody.RootElement.GetProperty("messages").ToString();
            Assert.Contains("local fixture fact", resumedMessages);
            Assert.Contains("Continue from the saved local tool result.", resumedMessages);
            await resumed.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task EditorSubmitsAnEditedMultilinePromptThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-editor-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "facts.txt"), "fixture facts");
        await File.WriteAllTextAsync(Path.Combine(root, "facts.md"), "alternate completion");
        var ttyPathFile = Path.Combine(root, "tty-path");
        await using var server = new RouterServer(chatResponse: "MULTILINE_PROMPT_OK");
        await using var terminal = new ManagerTerminal(root, agent, server.Origin, llamaProvider: true,
            sessionDirectory: Path.Combine(root, "sessions"), noTools: true, ttyPathFile: ttyPathFile);
        try
        {
            await terminal.WaitFrameAsync(frame => frame.Contains("qwen", StringComparison.Ordinal) &&
                !frame.Contains("llama.cpp models", StringComparison.Ordinal) &&
                !frame.Contains("Unload model?", StringComparison.Ordinal));
            const string originalPrompt = "Read facts.txt and answer with its content.\nSummarize the answer in one sentence.";
            await terminal.SendRawAsync("\u001b[200~" + originalPrompt + "\u001b[201~");
            await terminal.WaitTextAsync("Summarize the answer in one sentence.");
            await terminal.SendRawAsync(string.Concat(Enumerable.Repeat("\u001b[D", originalPrompt.Length)) + "Please ");
            await terminal.WaitTextAsync("Please Read facts.txt and answer with its content.");
            await ProcessTestHelpers.WaitForFileAsync(ttyPathFile);
            var ttyPath = (await File.ReadAllTextAsync(ttyPathFile)).Trim();
            var narrowMark = terminal.Mark;
            await ResizeLinuxPtyAsync(ttyPath, rows: 18, columns: 60);
            await terminal.SendRawAsync("\u001b[D\u001b[C");
            await terminal.WaitFrameAsync(frame => frame.Contains("Please Read facts.txt and answer with its content.", StringComparison.Ordinal) &&
                frame.Contains("Summarize the answer in one sentence.", StringComparison.Ordinal), narrowMark,
                rows: 18, columns: 60);
            var wideMark = terminal.Mark;
            await ResizeLinuxPtyAsync(ttyPath, rows: 30, columns: 100);
            await terminal.SendRawAsync("\u001b[D\u001b[C");
            await terminal.WaitFrameAsync(frame => frame.Contains("Please Read facts.txt and answer with its content.", StringComparison.Ordinal) &&
                frame.Contains("Summarize the answer in one sentence.", StringComparison.Ordinal), wideMark);
            await terminal.SendRawAsync("\r");
            await terminal.WaitFrameAsync(frame => frame.Contains("MULTILINE_PROMPT_OK", StringComparison.Ordinal) &&
                !frame.Contains("Working", StringComparison.Ordinal));

            var request = Assert.Single(server.Requests, item => item.Method == "POST" &&
                item.Path == "/v1/chat/completions");
            using var body = JsonDocument.Parse(request.Body!);
            var userMessage = body.RootElement.GetProperty("messages").EnumerateArray()
                .Single(message => message.GetProperty("role").GetString() == "user");
            var content = userMessage.GetProperty("content");
            var submittedPrompt = content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : string.Concat(content.EnumerateArray().Select(part => part.GetProperty("text").GetString()));
            Assert.Equal("Please Read facts.txt and answer with its content.\nSummarize the answer in one sentence.",
                submittedPrompt);

            var historyMark = terminal.Mark;
            await terminal.SendRawAsync("\u001b[A");
            var recalled = await terminal.WaitFrameAsync(frame =>
                Regex.Matches(frame, Regex.Escape("Read facts.txt and answer with its content.")).Count >= 2,
                historyMark);
            Assert.Contains("Summarize the answer in one sentence.", recalled);
            var resubmitMark = terminal.Mark;
            await terminal.SendRawAsync("\r");
            using (var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while (server.Requests.Count(item => item.Method == "POST" &&
                    item.Path == "/v1/chat/completions") < 2)
                    await Task.Delay(25, requestTimeout.Token);
            }
            var historyRequests = server.Requests.Where(item => item.Method == "POST" &&
                item.Path == "/v1/chat/completions").ToArray();
            Assert.Equal(2, historyRequests.Length);
            using var recalledBody = JsonDocument.Parse(historyRequests[1].Body!);
            var recalledUsers = recalledBody.RootElement.GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("role").GetString() == "user").ToArray();
            Assert.Equal(2, recalledUsers.Length);
            var recalledContent = recalledUsers[^1].GetProperty("content");
            var recalledPrompt = recalledContent.ValueKind == JsonValueKind.String
                ? recalledContent.GetString()
                : string.Concat(recalledContent.EnumerateArray().Select(part => part.GetProperty("text").GetString()));
            Assert.Equal(submittedPrompt, recalledPrompt);
            await terminal.WaitFrameAsync(frame => frame.Contains("MULTILINE_PROMPT_OK", StringComparison.Ordinal) &&
                Regex.Matches(frame, Regex.Escape("MULTILINE_PROMPT_OK")).Count >= 2 &&
                Regex.Matches(frame, Regex.Escape("Please Read facts.txt and answer with its content.")).Count >= 2 &&
                !frame.Contains("Working", StringComparison.Ordinal), resubmitMark);

            await terminal.SendRawAsync("@facts.tx\t");
            await terminal.WaitFrameAsync(frame => EditorInputLine(frame) == "@facts.txt" &&
                !frame.Contains("Complete input", StringComparison.Ordinal));
            var clearMark = terminal.Mark;
            await terminal.SendRawAsync("\u0001\u000b");
            await terminal.WaitFrameAsync(frame => !frame.Contains("@facts.txt", StringComparison.Ordinal), clearMark);
            await terminal.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StreamingTranscriptRemainsAnchoredAcrossScrollAndResizeThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-viewport-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var ttyPathFile = Path.Combine(root, "tty-path");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        var streamedRows = Enumerable.Range(0, 60).Select(index => $"STREAM_ROW_{index:D2}\n").ToArray();
        try
        {
            await using var server = new RouterServer(chatResponse: "FOLLOWUP_OK", streamedChatChunks: streamedRows,
                pauseAfterChunk: 35, pauseAgainAfterChunk: 45);
            await using var terminal = new ManagerTerminal(root, agent, server.Origin, llamaProvider: true,
                sessionDirectory: Path.Combine(root, "sessions"), noTools: true, ttyPathFile: ttyPathFile);
            try
            {
                await terminal.WaitTextAsync("qwen");
                await terminal.SendAsync("Stream a long answer.\n");
                await server.LongChatPaused.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await terminal.WaitFrameAsync(frame => frame.Contains("STREAM_ROW_34", StringComparison.Ordinal));

                var scrollMark = terminal.Mark;
                await terminal.SendRawAsync("\u001b[5~");
                var scrolled = await terminal.WaitFrameAsync(frame => FirstStreamRow(frame) == "STREAM_ROW_00", scrollMark);
                var anchor = FirstStreamRow(scrolled);
                Assert.Equal("STREAM_ROW_00", anchor);

                await ProcessTestHelpers.WaitForFileAsync(ttyPathFile);
                var ttyPath = (await File.ReadAllTextAsync(ttyPathFile)).Trim();
                using (var resize = Process.Start(new ProcessStartInfo("stty")
                {
                    RedirectStandardError = true,
                    ArgumentList = { "-F", ttyPath, "rows", "18", "cols", "60" }
                })!)
                {
                    var error = resize.StandardError.ReadToEndAsync();
                    await resize.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(resize.ExitCode == 0, await error);
                    Assert.Equal("", await error);
                }

                var resizeMark = terminal.Mark;
                var resized = await terminal.WaitFrameAsync(frame => FirstStreamRow(frame) == anchor &&
                    frame.Contains("↓ Jump to latest message", StringComparison.Ordinal),
                    resizeMark, rows: 18, columns: 60);
                Assert.Equal(anchor, FirstStreamRow(resized));
                Assert.Contains("↓ Jump to latest message", resized, StringComparison.Ordinal);

                var continuedMark = terminal.Mark;
                server.ReleaseLongChatStream();
                await server.LongChatSecondPaused.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var continued = await terminal.WaitFrameAsync(frame => FirstStreamRow(frame) == anchor &&
                    frame.Contains("↓ Jump to latest message", StringComparison.Ordinal) &&
                    !frame.Contains("STREAM_ROW_44", StringComparison.Ordinal),
                    continuedMark, rows: 18, columns: 60);
                Assert.Equal(anchor, FirstStreamRow(continued));
                Assert.Contains("↓ Jump to latest message", continued, StringComparison.Ordinal);

                var latestMark = terminal.Mark;
                await terminal.SendRawAsync("\u001b[57424;5u");
                var latest = await terminal.WaitFrameAsync(frame => frame.Contains("STREAM_ROW_44", StringComparison.Ordinal) &&
                    !frame.Contains("↓ Jump to latest message", StringComparison.Ordinal), latestMark,
                    rows: 18, columns: 60);
                Assert.NotEqual(anchor, FirstStreamRow(latest));

                server.ReleaseLongChatSecondPause();
                await server.LongChatCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await terminal.WaitFrameAsync(frame => frame.Contains("STREAM_ROW_59", StringComparison.Ordinal) &&
                    !frame.Contains("↑", StringComparison.Ordinal) && !frame.Contains("Working", StringComparison.Ordinal),
                    terminal.Mark - 1, rows: 18, columns: 60);

                await terminal.SendAsync("follow up prompt\n");
                await terminal.WaitFrameAsync(frame => frame.Contains("FOLLOWUP_OK", StringComparison.Ordinal) &&
                    !frame.Contains("Working", StringComparison.Ordinal), rows: 18, columns: 60);
                await terminal.QuitAsync();

                var requests = server.Requests.Where(item => item.Method == "POST" &&
                    item.Path == "/v1/chat/completions").ToArray();
                Assert.Equal(2, requests.Length);
                using var followUp = JsonDocument.Parse(requests[1].Body!);
                var history = followUp.RootElement.GetProperty("messages").ToString();
                Assert.Contains("Stream a long answer.", history, StringComparison.Ordinal);
                Assert.Contains("STREAM_ROW_00", history, StringComparison.Ordinal);
                Assert.Contains("STREAM_ROW_59", history, StringComparison.Ordinal);
                for (var index = 0; index < streamedRows.Length; index++)
                    Assert.Single(Regex.Matches(history, $"STREAM_ROW_{index:D2}").Cast<Match>());
                Assert.Contains("follow up prompt", history, StringComparison.Ordinal);
            }
            finally
            {
                server.ReleaseLongChatStream();
                server.ReleaseLongChatSecondPause();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string? FirstStreamRow(string frame)
    {
        var match = Regex.Match(frame, "STREAM_ROW_\\d+");
        return match.Success ? match.Value : null;
    }

    [Fact]
    public async Task CancellingLocalModelStreamAfterVisibleTextSettlesAndAcceptsNextPrompt()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-stream-cancel-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await using var server = new RouterServer(chatResponse: "AFTER_CANCEL_OK", holdChatAfterPartial: true);
        await using var terminal = new ManagerTerminal(root, agent, server.Origin, llamaProvider: true,
            sessionDirectory: sessions);
        try
        {
            await terminal.WaitTextAsync("qwen");
            var mark = terminal.Mark;
            await terminal.SendAsync("cancel the partial local response\n");
            await server.ChatPartialSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await terminal.WaitTextAsync("LLAMA_PARTIAL_VISIBLE", mark);

            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            await terminal.WaitFrameAsync(frame => frame.Contains("Operation aborted", StringComparison.Ordinal) &&
                !frame.Contains("Working", StringComparison.Ordinal), mark);
            server.ReleaseChatStream();

            mark = terminal.Mark;
            await terminal.SendAsync("continue after interruption\n");
            await terminal.WaitTextAsync("AFTER_CANCEL_OK", mark);
            await terminal.WaitFrameAsync(frame => frame.Contains("AFTER_CANCEL_OK", StringComparison.Ordinal) &&
                !frame.Contains("Working", StringComparison.Ordinal), mark);
            await terminal.QuitAsync();

            var requests = server.Requests.Where(item => item.Method == "POST" &&
                item.Path == "/v1/chat/completions").ToArray();
            Assert.Equal(2, requests.Length);
            Assert.Contains("cancel the partial local response", requests[0].Body, StringComparison.Ordinal);
            Assert.Contains("continue after interruption", requests[1].Body, StringComparison.Ordinal);

            var sessionPath = Assert.Single(Directory.EnumerateFiles(sessions, "*.session.json"));
            var persisted = await new PiSharp.Runtime.Sessions.ConversationStore(root, sessions).LoadAsync(sessionPath);
            Assert.Contains(persisted.Tree.Entries, entry => entry.Type == "interrupted" &&
                entry.Payload.GetProperty("partialText").GetString() == "LLAMA_PARTIAL_VISIBLE");
            Assert.Contains(persisted.Tree.Entries, entry => entry.Type == "run_finished" &&
                entry.Payload.GetProperty("completed").GetBoolean());
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

    private static async Task ResizeLinuxPtyAsync(string ttyPath, int rows, int columns)
    {
        using var resize = Process.Start(new ProcessStartInfo("stty")
        {
            RedirectStandardError = true,
            ArgumentList = { "-F", ttyPath, "rows", rows.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "cols", columns.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        })!;
        var error = resize.StandardError.ReadToEndAsync();
        await resize.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(resize.ExitCode == 0, await error);
        Assert.Equal("", await error);
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
        private readonly string? _chatResponse;
        private readonly string? _resumedChatResponse;
        private readonly bool _readToolCallFirst;
        private readonly bool _holdChatAfterPartial;
        private readonly string[]? _streamedChatChunks;
        private readonly int _pauseAfterChunk;
        private readonly int _pauseAgainAfterChunk;
        private int _chatRequestCount;
        private readonly ConcurrentBag<Task> _background = [];
        private readonly TaskCompletionSource _downloadRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _loadProgressRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _chatRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _longChatRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _longChatSecondRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _catalogRequests;
        public ConcurrentQueue<(string Method, string Path, string? Authorization, string? Body)> Requests { get; } = new();
        public TaskCompletionSource LoadRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RestoreLoadRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChatPartialSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LongChatPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LongChatSecondPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LongChatCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            bool sleepingQwen = false, bool holdRestore = false, string? chatResponse = null,
            bool readToolCallFirst = false, bool holdChatAfterPartial = false, string? resumedChatResponse = null,
            string[]? streamedChatChunks = null, int pauseAfterChunk = 0, int pauseAgainAfterChunk = 0)
        {
            _holdLoad = holdLoad;
            _failFirstCatalog = failFirstCatalog;
            _holdDownload = holdDownload;
            _holdRestore = holdRestore;
            _chatResponse = chatResponse;
            _resumedChatResponse = resumedChatResponse;
            _readToolCallFirst = readToolCallFirst;
            _holdChatAfterPartial = holdChatAfterPartial;
            _streamedChatChunks = streamedChatChunks;
            _pauseAfterChunk = pauseAfterChunk;
            _pauseAgainAfterChunk = pauseAgainAfterChunk;
            if (sleepingQwen) Statuses["qwen"] = "sleeping";
            lock (s_routerEndpointGate)
            {
                using var reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                Origin = "http://127.0.0.1:" + ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();
                _listener.Prefixes.Add(Origin + "/");
                _listener.Start();
            }
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
                    else if (context.Request.HttpMethod == "GET" && path == "/v1/models")
                        response = "{\"data\":[{\"id\":\"fixture-model\"}]}";
                    else if (context.Request.HttpMethod == "GET" && path == "/models/sse")
                    {
                        contentType = "text/event-stream";
                        if (_holdDownload)
                        {
                            _background.Add(RespondDownloadProgressAsync(context));
                            continue;
                        }
                        if (_holdLoad)
                        {
                            _background.Add(RespondLoadProgressAsync(context));
                            continue;
                        }
                        response = "data: {\"model\":\"owner/model:Q4_K_M\",\"event\":\"download_finished\",\"data\":{}}\n\n";
                    }
                    else if (context.Request.HttpMethod == "GET" && path.StartsWith("/props", StringComparison.Ordinal))
                        response = "{}";
                    else if (context.Request.HttpMethod == "POST" && path == "/v1/chat/completions" &&
                             _holdChatAfterPartial && Interlocked.Increment(ref _chatRequestCount) == 1)
                    {
                        using var payload = JsonDocument.Parse(body!);
                        var model = payload.RootElement.GetProperty("model").GetString()!;
                        var first = new
                        {
                            id = "chatcmpl_local_cancel",
                            @object = "chat.completion.chunk",
                            created = 1,
                            model,
                            choices = new[] { new
                            {
                                index = 0,
                                delta = new { role = "assistant", content = "LLAMA_PARTIAL_VISIBLE" },
                                finish_reason = (string?)null
                            } }
                        };
                        context.Response.ContentType = "text/event-stream";
                        context.Response.SendChunked = true;
                        var partialBytes = Encoding.UTF8.GetBytes($"data: {JsonSerializer.Serialize(first)}\n\n");
                        await context.Response.OutputStream.WriteAsync(partialBytes, _shutdown.Token);
                        await context.Response.OutputStream.FlushAsync(_shutdown.Token);
                        ChatPartialSent.TrySetResult();
                        await _chatRelease.Task.WaitAsync(_shutdown.Token);
                        try { context.Response.Close(); }
                        catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException) { }
                        continue;
                    }
                    else if (context.Request.HttpMethod == "POST" && path == "/v1/chat/completions" &&
                             (_chatResponse is not null || _readToolCallFirst))
                    {
                        using var payload = JsonDocument.Parse(body!);
                        var model = payload.RootElement.GetProperty("model").GetString()!;
                        var chatRequestNumber = _readToolCallFirst || _streamedChatChunks is not null
                            ? Interlocked.Increment(ref _chatRequestCount) : 0;
                        var responseText = _readToolCallFirst && chatRequestNumber >= 3
                            ? _resumedChatResponse ?? _chatResponse
                            : _chatResponse;
                        if (_streamedChatChunks is { } chunks && chatRequestNumber == 1)
                        {
                            context.Response.ContentType = "text/event-stream";
                            context.Response.SendChunked = true;
                            context.Response.KeepAlive = false;
                            await using var writer = new StreamWriter(context.Response.OutputStream);
                            for (var index = 0; index < chunks.Length; index++)
                            {
                                var chunk = new
                                {
                                    id = "chatcmpl_long_stream",
                                    @object = "chat.completion.chunk",
                                    created = 1,
                                    model,
                                    choices = new[] { new
                                    {
                                        index = 0,
                                        delta = new { role = "assistant", content = chunks[index] },
                                        finish_reason = (string?)null
                                    } }
                                };
                                await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                                await writer.FlushAsync(_shutdown.Token);
                                if (index + 1 == _pauseAfterChunk)
                                {
                                    LongChatPaused.TrySetResult();
                                    await _longChatRelease.Task.WaitAsync(_shutdown.Token);
                                }
                                if (index + 1 == _pauseAgainAfterChunk)
                                {
                                    LongChatSecondPaused.TrySetResult();
                                    await _longChatSecondRelease.Task.WaitAsync(_shutdown.Token);
                                }
                            }
                            var last = new
                            {
                                id = "chatcmpl_long_stream",
                                @object = "chat.completion.chunk",
                                created = 1,
                                model,
                                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
                            };
                            await writer.WriteAsync($"data: {JsonSerializer.Serialize(last)}\n\ndata: [DONE]\n\n");
                            await writer.FlushAsync(_shutdown.Token);
                            context.Response.Close();
                            LongChatCompleted.TrySetResult();
                            continue;
                        }
                        if (_readToolCallFirst && chatRequestNumber == 1)
                        {
                            var first = new
                            {
                                id = "chatcmpl_local",
                                @object = "chat.completion.chunk",
                                created = 1,
                                model,
                                choices = new[] { new
                                {
                                    index = 0,
                                    delta = new { role = "assistant", tool_calls = new[] { new
                                    {
                                        index = 0,
                                        id = "read-1",
                                        type = "function",
                                        function = new { name = "read", arguments = "{\"path\":\"facts.txt\"}" }
                                    } } },
                                    finish_reason = (string?)null
                                } }
                            };
                            var last = new
                            {
                                id = "chatcmpl_local",
                                @object = "chat.completion.chunk",
                                created = 1,
                                model,
                                choices = new[] { new { index = 0, delta = new { }, finish_reason = "tool_calls" } }
                            };
                            response = $"data: {JsonSerializer.Serialize(first)}\n\ndata: {JsonSerializer.Serialize(last)}\n\ndata: [DONE]\n\n";
                        }
                        else
                        {
                            var first = new
                            {
                                id = "chatcmpl_local",
                                @object = "chat.completion.chunk",
                                created = 1,
                                model,
                                choices = new[] { new
                                {
                                    index = 0,
                                    delta = new { role = "assistant", content = responseText },
                                    finish_reason = (string?)null
                                } }
                            };
                            var last = new
                            {
                                id = "chatcmpl_local",
                                @object = "chat.completion.chunk",
                                created = 1,
                                model,
                                choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
                            };
                            response = $"data: {JsonSerializer.Serialize(first)}\n\ndata: {JsonSerializer.Serialize(last)}\n\ndata: [DONE]\n\n";
                        }
                        contentType = "text/event-stream";
                    }
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

        private async Task RespondLoadProgressAsync(HttpListenerContext context)
        {
            try
            {
                await _loadProgressRelease.Task.WaitAsync(_shutdown.Token);
                const string response = "data: {\"model\":\"target\",\"event\":\"model_status\",\"data\":{\"status\":\"loading\",\"progress\":{\"current\":\"loading_model\",\"stages\":[\"loading_model\",\"loading_context\"],\"value\":0.5}}}\n\n";
                var bytes = Encoding.UTF8.GetBytes(response);
                context.Response.ContentType = "text/event-stream";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, _shutdown.Token);
                context.Response.Close();
            }
            catch (Exception error) when (_shutdown.IsCancellationRequested &&
                error is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException)
            { }
            finally
            {
                try { context.Response.Close(); }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or IOException) { }
            }
        }

        public void ReleaseLoadProgress() => _loadProgressRelease.TrySetResult();

        public void ReleaseChatStream() => _chatRelease.TrySetResult();
        public void ReleaseLongChatStream() => _longChatRelease.TrySetResult();
        public void ReleaseLongChatSecondPause() => _longChatSecondRelease.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            _restoreLoadRelease.TrySetResult();
            _chatRelease.TrySetResult();
            _longChatRelease.TrySetResult();
            _longChatSecondRelease.TrySetResult();
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
            string? hfToken = null, bool llamaProvider = false, string? sessionDirectory = null, bool noTools = true,
            bool continueSession = false, string? ttyPathFile = null, bool offline = false)
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
                ? " --provider llama.cpp --model qwen"
                : " --provider fixture --model fixture-model --offline";
            arguments += sessionDirectory is null ? " --no-session" : " --session-dir " + ShellQuote(sessionDirectory);
            if (continueSession) arguments += " --continue";
            if (noTools) arguments += " --no-tools";
            if (offline && llamaProvider) arguments += " --offline";
            var terminalSetup = "stty rows 30 cols 100;";
            if (ttyPathFile is not null) terminalSetup += " tty > " + ShellQuote(ttyPathFile) + ";";
            start.ArgumentList.Add(terminalSetup + " exec dotnet " + ShellQuote(typeof(CliArguments).Assembly.Location) + arguments);
            start.ArgumentList.Add("/dev/null");
            foreach (var name in new[] { "LLAMA_BASE_URL", "LLAMA_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL",
                         "PISHARP_MODEL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH",
                         "PISHARP_BLOCKED_KEY" })
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
            await _process.StandardInput.WriteAsync(input.Replace('\n', '\r'));
            await _process.StandardInput.FlushAsync();
        }

        public async Task SendRawAsync(string input)
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

        public Task<string> WaitFrameAsync(Func<string, bool> predicate, int after = 0, int rows = 30, int columns = 100) => WaitAsync(output =>
        {
            foreach (var frame in TerminalOutputFrameReader.Read(output, rows, columns))
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

        public async Task QuitAsync(int expectedExitCode = 0)
        {
            await SendAsync("/quit\n");
            _process.StandardInput.Close();
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException error)
            {
                throw new TimeoutException("The llama.cpp manager did not accept /quit.\n" + Output[^Math.Min(Output.Length, 5000)..], error);
            }
            await _readOutput;
            Assert.Equal(expectedExitCode, _process.ExitCode);
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
