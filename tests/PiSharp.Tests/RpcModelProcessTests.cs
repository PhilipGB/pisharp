using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class RpcModelProcessTests
{
    [Fact]
    public async Task ModelCommandsProjectPiModelMetadataAndApplySparseDefaultsInTheCliProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-model-projection-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","api":"openai-completions","compat":{"supportsDeveloperRole":true,"openRouterRouting":{"zdr":false,"maxPrice":0.5}},"apiKeyEnv":"PISHARP_FIXTURE_KEY","headers":{"Authorization":"Bearer fixture-only-secret"},"models":[
             {"id":"rich","name":"Rich model","baseUrl":"http://127.0.0.1:1/v1/model-rich","api":"openai-responses","contextWindow":8192,"maxTokens":2048,"reasoning":true,"thinkingLevelMap":{"off":"none","high":"extended","max":null},"input":["text","image"],"inputLimits":{"maxRequestBytes":120000,"images":{"maxPerMessage":4,"maxPerRequest":8,"resize":{"maxWidth":512,"maxHeight":768,"maxBytes":1048576,"jpegQuality":80}}},"cost":{"input":1.25,"output":4,"cacheRead":0.2,"cacheWrite":0.5,"tiers":[{"inputTokensAbove":100,"input":2,"output":5,"cacheRead":0.3,"cacheWrite":0.6}]},"promptCache":{"short":300,"long":1800},"samplingParams":{"temperature":0.2,"top_p":0.9},"compat":{"supportsStore":false,"openRouterRouting":{"zdr":true,"allowFallbacks":false}},"headers":{"X-Model-Secret":"fixture-only-secret"}},
             {"id":"sparse"}]}}}
            """);
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var name in start.Environment.Keys.Where(name => name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(name);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "sparse", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-secret";
            process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stderr = process.StandardError.ReadToEndAsync();
            await WriteCommandAsync(process, new { id = "available", type = "get_available_models" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "unavailable", type = "set_model", provider = "openai", modelId = "gpt-4o-mini" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "select", type = "set_model", provider = "fixture", modelId = "rich" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "cycle", type = "cycle_model" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "state", type = "get_state" }, timeout.Token);
            var lines = await ReadResponsesAsync(process, ["available", "unavailable", "select", "cycle", "state"], timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            var output = string.Join('\n', lines);
            Assert.DoesNotContain("fixture-only-secret", output, StringComparison.Ordinal);

            using var available = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"available\"", StringComparison.Ordinal)));
            var models = available.RootElement.GetProperty("data").GetProperty("models");
            Assert.Equal(2, models.GetArrayLength());
            var rich = models.EnumerateArray().Single(model => model.GetProperty("id").GetString() == "rich");
            Assert.Equal("Rich model", rich.GetProperty("name").GetString());
            Assert.Equal("openai-responses", rich.GetProperty("api").GetString());
            Assert.Equal("fixture", rich.GetProperty("provider").GetString());
            Assert.Equal("http://127.0.0.1:1/v1/model-rich", rich.GetProperty("baseUrl").GetString());
            Assert.True(rich.GetProperty("reasoning").GetBoolean());
            Assert.Equal("none", rich.GetProperty("thinkingLevelMap").GetProperty("off").GetString());
            Assert.Equal(JsonValueKind.Null, rich.GetProperty("thinkingLevelMap").GetProperty("max").ValueKind);
            Assert.Equal(["text", "image"], rich.GetProperty("input").EnumerateArray().Select(item => item.GetString()));
            var limits = rich.GetProperty("inputLimits");
            Assert.Equal(120000, limits.GetProperty("maxRequestBytes").GetInt32());
            Assert.Equal(4, limits.GetProperty("images").GetProperty("maxPerMessage").GetInt32());
            Assert.Equal(8, limits.GetProperty("images").GetProperty("maxPerRequest").GetInt32());
            Assert.Equal(512, limits.GetProperty("images").GetProperty("resize").GetProperty("maxWidth").GetInt32());
            var cost = rich.GetProperty("cost");
            Assert.Equal(1.25m, cost.GetProperty("input").GetDecimal());
            Assert.Equal(0.2m, cost.GetProperty("cacheRead").GetDecimal());
            Assert.Equal(0.5m, cost.GetProperty("cacheWrite").GetDecimal());
            Assert.Equal(100, cost.GetProperty("tiers")[0].GetProperty("inputTokensAbove").GetInt32());
            Assert.Equal(0.6m, cost.GetProperty("tiers")[0].GetProperty("cacheWrite").GetDecimal());
            Assert.Equal(300, rich.GetProperty("promptCache").GetProperty("short").GetInt32());
            Assert.Equal(1800, rich.GetProperty("promptCache").GetProperty("long").GetInt32());
            Assert.Equal(0.2, rich.GetProperty("samplingParams").GetProperty("temperature").GetDouble());
            Assert.False(rich.GetProperty("compat").GetProperty("supportsStore").GetBoolean());
            Assert.True(rich.GetProperty("compat").GetProperty("supportsDeveloperRole").GetBoolean());
            Assert.True(rich.GetProperty("compat").GetProperty("openRouterRouting").GetProperty("zdr").GetBoolean());
            Assert.False(rich.GetProperty("compat").GetProperty("openRouterRouting").GetProperty("allowFallbacks").GetBoolean());
            Assert.Equal(0.5, rich.GetProperty("compat").GetProperty("openRouterRouting").GetProperty("maxPrice").GetDouble());
            Assert.False(rich.TryGetProperty("headers", out _));
            Assert.False(rich.TryGetProperty("Owner", out _));
            Assert.False(rich.TryGetProperty("Status", out _));
            Assert.False(rich.TryGetProperty("Available", out _));

            using var unavailable = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"unavailable\"", StringComparison.Ordinal)));
            Assert.False(unavailable.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Model not found: openai/gpt-4o-mini", unavailable.RootElement.GetProperty("error").GetString());

            using var selected = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"select\"", StringComparison.Ordinal)));
            var selectedModel = selected.RootElement.GetProperty("data");
            Assert.Equal("rich", selectedModel.GetProperty("id").GetString());
            Assert.Equal("http://127.0.0.1:1/v1/model-rich", selectedModel.GetProperty("baseUrl").GetString());

            using var cycled = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"cycle\"", StringComparison.Ordinal)));
            var sparse = cycled.RootElement.GetProperty("data").GetProperty("model");
            Assert.Equal("sparse", sparse.GetProperty("id").GetString());
            Assert.Equal("sparse", sparse.GetProperty("name").GetString());
            Assert.Equal("openai-completions", sparse.GetProperty("api").GetString());
            Assert.Equal("http://127.0.0.1:1/v1", sparse.GetProperty("baseUrl").GetString());
            Assert.False(sparse.GetProperty("reasoning").GetBoolean());
            Assert.Equal(["text"], sparse.GetProperty("input").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(128000, sparse.GetProperty("contextWindow").GetInt32());
            Assert.Equal(16384, sparse.GetProperty("maxTokens").GetInt32());
            Assert.Equal(0, sparse.GetProperty("cost").GetProperty("input").GetDecimal());
            Assert.Equal(0, sparse.GetProperty("cost").GetProperty("cacheRead").GetDecimal());
            Assert.Equal("off", cycled.RootElement.GetProperty("data").GetProperty("thinkingLevel").GetString());

            using var state = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"state\"", StringComparison.Ordinal)));
            Assert.Equal("sparse", state.RootElement.GetProperty("data").GetProperty("model").GetProperty("id").GetString());
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RpcQueueModesPersistAndAppearInStateInTheCliProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-queue-mode-process-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-model","name":"Fixture Model","contextWindow":8192,"maxTokens":2048,"reasoning":true,"input":["text","image"],"api":"openai-completions","cost":{"input":1.25,"output":4,"cacheRead":0.2,"cacheWrite":0.5},"inputLimits":{"images":{"resize":{"maxWidth":512,"maxBytes":1048576,"jpegQuality":80}}}}]}}}
            """);
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "fixture-model", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stderr = process.StandardError.ReadToEndAsync();
            await WriteCommandAsync(process, new { id = "steering-all", type = "set_steering_mode", mode = "all" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "follow-one", type = "set_follow_up_mode", mode = "one-at-a-time" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "invalid", type = "set_follow_up_mode", mode = "never" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "compaction-off", type = "set_auto_compaction", enabled = false }, timeout.Token);
            await WriteCommandAsync(process, new { id = "compaction-invalid", type = "set_auto_compaction", enabled = "false" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "state", type = "get_state" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "compaction-on", type = "set_auto_compaction", enabled = true }, timeout.Token);
            await WriteCommandAsync(process, new { id = "state-enabled", type = "get_state" }, timeout.Token);
            var lines = await ReadResponsesAsync(process,
                ["steering-all", "follow-one", "invalid", "compaction-off", "compaction-invalid", "state", "compaction-on", "state-enabled"], timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            using var steeringResponse = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"steering-all\"", StringComparison.Ordinal)));
            using var followResponse = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"follow-one\"", StringComparison.Ordinal)));
            using var invalidResponse = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"invalid\"", StringComparison.Ordinal)));
            using var compactionOffResponse = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"compaction-off\"", StringComparison.Ordinal)));
            using var compactionInvalidResponse = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"compaction-invalid\"", StringComparison.Ordinal)));
            using var compactionOnResponse = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"compaction-on\"", StringComparison.Ordinal)));
            Assert.True(steeringResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.True(followResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.False(invalidResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.True(compactionOffResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.False(compactionInvalidResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.True(compactionOnResponse.RootElement.GetProperty("success").GetBoolean());
            using var state = JsonDocument.Parse(Assert.Single(lines, line => line.Contains("\"id\":\"state\"", StringComparison.Ordinal)));
            var stateData = state.RootElement.GetProperty("data");
            Assert.Equal(["model", "thinkingLevel", "isStreaming", "isCompacting", "steeringMode", "followUpMode",
                    "sessionId", "autoCompactionEnabled", "messageCount", "pendingMessageCount"],
                stateData.EnumerateObject().Select(property => property.Name));
            Assert.Equal("all", stateData.GetProperty("steeringMode").GetString());
            Assert.Equal("one-at-a-time", stateData.GetProperty("followUpMode").GetString());
            Assert.False(stateData.GetProperty("isStreaming").GetBoolean());
            Assert.False(stateData.GetProperty("isCompacting").GetBoolean());
            Assert.False(stateData.GetProperty("autoCompactionEnabled").GetBoolean());
            Assert.Equal(0, stateData.GetProperty("messageCount").GetInt32());
            Assert.Equal(0, stateData.GetProperty("pendingMessageCount").GetInt32());
            Assert.False(stateData.TryGetProperty("sessionFile", out _));
            Assert.False(stateData.TryGetProperty("sessionName", out _));
            var model = stateData.GetProperty("model");
            Assert.Equal(["id", "name", "api", "provider", "baseUrl", "reasoning", "input", "inputLimits",
                    "cost", "contextWindow", "maxTokens"],
                model.EnumerateObject().Select(property => property.Name));
            Assert.Equal("fixture-model", model.GetProperty("id").GetString());
            Assert.Equal("Fixture Model", model.GetProperty("name").GetString());
            Assert.Equal("openai-completions", model.GetProperty("api").GetString());
            Assert.Equal("fixture", model.GetProperty("provider").GetString());
            Assert.Equal("http://127.0.0.1:1/v1", model.GetProperty("baseUrl").GetString());
            Assert.Equal(["text", "image"], model.GetProperty("input").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(1.25m, model.GetProperty("cost").GetProperty("input").GetDecimal());
            Assert.Equal(4m, model.GetProperty("cost").GetProperty("output").GetDecimal());
            Assert.Equal(0.2m, model.GetProperty("cost").GetProperty("cacheRead").GetDecimal());
            Assert.Equal(0.5m, model.GetProperty("cost").GetProperty("cacheWrite").GetDecimal());
            Assert.True(model.GetProperty("reasoning").GetBoolean());
            Assert.Equal(8192, model.GetProperty("contextWindow").GetInt32());
            Assert.Equal(2048, model.GetProperty("maxTokens").GetInt32());
            var resize = model.GetProperty("inputLimits").GetProperty("images").GetProperty("resize");
            Assert.Equal(512, resize.GetProperty("maxWidth").GetInt32());
            Assert.Equal(1048576, resize.GetProperty("maxBytes").GetInt32());
            Assert.Equal(80, resize.GetProperty("jpegQuality").GetInt32());
            using var enabledState = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"state-enabled\"", StringComparison.Ordinal)));
            Assert.True(enabledState.RootElement.GetProperty("data").GetProperty("autoCompactionEnabled").GetBoolean());
            using var savedSettings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(agent, "settings.json")));
            Assert.Equal("all", savedSettings.RootElement.GetProperty("steeringMode").GetString());
            Assert.Equal("one-at-a-time", savedSettings.RootElement.GetProperty("followUpMode").GetString());
            Assert.True(savedSettings.RootElement.GetProperty("compaction").GetProperty("enabled").GetBoolean());
            Assert.DoesNotContain(lines, line => line.Contains("\"type\":\"error\"", StringComparison.Ordinal));
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RpcStartupRestoresThinkingLevelFromTheSelectedSessionBranch()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-thinking-restore-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-reasoning","reasoning":true}]}}}
            """);
        var sessionPath = Path.Combine(root, "restore.session.json");
        var session = new ConversationSession(root, "fixture-reasoning", "http://127.0.0.1:1/v1", "fixture");
        session.AppendThinkingLevelChange("max");
        await File.WriteAllTextAsync(sessionPath, session.ToJson());
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--session", sessionPath, "--offline" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stderr = process.StandardError.ReadToEndAsync();
            await WriteCommandAsync(process, new { id = "set-name", type = "set_session_name", name = "restored fixture" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "restored-state", type = "get_state" }, timeout.Token);
            var lines = await ReadResponsesAsync(process, ["set-name", "restored-state"], timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            using var response = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"restored-state\"", StringComparison.Ordinal)));
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            var state = response.RootElement.GetProperty("data");
            Assert.Equal(["id", "name", "api", "provider", "baseUrl", "reasoning", "input", "cost",
                    "contextWindow", "maxTokens"],
                state.GetProperty("model").EnumerateObject().Select(property => property.Name));
            Assert.Equal("fixture-reasoning", state.GetProperty("model").GetProperty("id").GetString());
            Assert.Equal("max", state.GetProperty("thinkingLevel").GetString());
            Assert.Equal(Path.GetFullPath(sessionPath), state.GetProperty("sessionFile").GetString());
            Assert.Equal("restored fixture", state.GetProperty("sessionName").GetString());
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RpcModelAndThinkingCommandsSelectAndCycleTheRunningRuntime()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-model-process-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-model","reasoning":false},{"id":"fixture-next","reasoning":false},{"id":"fixture-reasoning","reasoning":true}]}}}
            """);
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixture", "--model", "fixture-model", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stderr = process.StandardError.ReadToEndAsync();
            await WriteCommandAsync(process, new
            {
                id = "switch",
                type = "set_model",
                provider = "fixture",
                modelId = "fixture-reasoning"
            }, timeout.Token);
            await WriteCommandAsync(process, new
            {
                id = "cycle",
                type = "cycle_model"
            }, timeout.Token);
            await WriteCommandAsync(process, new
            {
                id = "unknown",
                type = "set_model",
                provider = "fixture",
                modelId = "not-configured"
            }, timeout.Token);
            await WriteCommandAsync(process, new { id = "state", type = "get_state" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "unsupported-thinking", type = "set_thinking_level", level = "high" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "cycle-no-thinking", type = "cycle_thinking_level" }, timeout.Token);
            await WriteCommandAsync(process, new
            {
                id = "reasoning-model",
                type = "set_model",
                provider = "fixture",
                modelId = "fixture-reasoning"
            }, timeout.Token);
            await WriteCommandAsync(process, new { id = "thinking-levels", type = "get_available_thinking_levels" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "set-max", type = "set_thinking_level", level = "max" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "set-max-again", type = "set_thinking_level", level = "max" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "state-max", type = "get_state" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "thinking-entries", type = "get_entries" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "cycle-thinking", type = "cycle_thinking_level" }, timeout.Token);
            await WriteCommandAsync(process, new { id = "state-off", type = "get_state" }, timeout.Token);
            var lines = await ReadResponsesAsync(process,
                ["switch", "cycle", "unknown", "state", "unsupported-thinking", "cycle-no-thinking", "reasoning-model",
                    "thinking-levels", "set-max", "set-max-again", "state-max", "thinking-entries", "cycle-thinking", "state-off"], timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
            using var switched = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"switch\"", StringComparison.Ordinal)));
            Assert.True(switched.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("fixture-reasoning", switched.RootElement.GetProperty("data").GetProperty("id").GetString());
            Assert.Equal("fixture", switched.RootElement.GetProperty("data").GetProperty("provider").GetString());
            using var cycled = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"cycle\"", StringComparison.Ordinal)));
            Assert.True(cycled.RootElement.GetProperty("success").GetBoolean());
            var cycleData = cycled.RootElement.GetProperty("data");
            Assert.Equal("fixture-model", cycleData.GetProperty("model").GetProperty("id").GetString());
            Assert.Equal("fixture", cycleData.GetProperty("model").GetProperty("provider").GetString());
            Assert.Equal("off", cycleData.GetProperty("thinkingLevel").GetString());
            Assert.False(cycleData.GetProperty("isScoped").GetBoolean());
            using var unknown = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"unknown\"", StringComparison.Ordinal)));
            Assert.False(unknown.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("Model not found: fixture/not-configured", unknown.RootElement.GetProperty("error").GetString());
            using var state = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"state\"", StringComparison.Ordinal)));
            Assert.Equal("fixture-model", state.RootElement.GetProperty("data").GetProperty("model").GetProperty("id").GetString());
            Assert.Equal("off", state.RootElement.GetProperty("data").GetProperty("thinkingLevel").GetString());
            using var unsupportedThinking = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"unsupported-thinking\"", StringComparison.Ordinal)));
            Assert.True(unsupportedThinking.RootElement.GetProperty("success").GetBoolean());
            using var noThinking = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"cycle-no-thinking\"", StringComparison.Ordinal)));
            Assert.Equal(JsonValueKind.Null, noThinking.RootElement.GetProperty("data").ValueKind);
            using var reasoningModel = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"reasoning-model\"", StringComparison.Ordinal)));
            Assert.Equal("fixture-reasoning", reasoningModel.RootElement.GetProperty("data").GetProperty("id").GetString());
            using var levels = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"thinking-levels\"", StringComparison.Ordinal)));
            Assert.Equal(["off", "minimal", "low", "medium", "high", "xhigh", "max"],
                levels.RootElement.GetProperty("data").GetProperty("levels").EnumerateArray().Select(level => level.GetString()));
            using var setMax = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"set-max\"", StringComparison.Ordinal)));
            Assert.True(setMax.RootElement.GetProperty("success").GetBoolean());
            Assert.False(setMax.RootElement.TryGetProperty("data", out _));
            using var setMaxAgain = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"set-max-again\"", StringComparison.Ordinal)));
            Assert.True(setMaxAgain.RootElement.GetProperty("success").GetBoolean());
            var thinkingEvents = lines.Where(line => line.Contains("\"type\":\"thinking_level_changed\"", StringComparison.Ordinal))
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Equal(["max", "off"], thinkingEvents.Select(item => item.RootElement.GetProperty("level").GetString()));
                Assert.All(thinkingEvents, item => Assert.Equal(["type", "level"],
                    item.RootElement.EnumerateObject().Select(property => property.Name)));
                var maxEventIndex = lines.FindIndex(line => line.Contains("\"type\":\"thinking_level_changed\"", StringComparison.Ordinal) &&
                    line.Contains("\"level\":\"max\"", StringComparison.Ordinal));
                var setMaxResponseIndex = lines.FindIndex(line => line.Contains("\"id\":\"set-max\"", StringComparison.Ordinal));
                var setMaxAgainResponseIndex = lines.FindIndex(line => line.Contains("\"id\":\"set-max-again\"", StringComparison.Ordinal));
                Assert.True(maxEventIndex >= 0 && maxEventIndex < setMaxResponseIndex);
                Assert.True(setMaxAgainResponseIndex > setMaxResponseIndex);
                var offEventIndex = lines.FindIndex(line => line.Contains("\"type\":\"thinking_level_changed\"", StringComparison.Ordinal) &&
                    line.Contains("\"level\":\"off\"", StringComparison.Ordinal));
                var cycleResponseIndex = lines.FindIndex(line => line.Contains("\"id\":\"cycle-thinking\"", StringComparison.Ordinal));
                Assert.True(offEventIndex >= 0 && offEventIndex < cycleResponseIndex);
            }
            finally { foreach (var item in thinkingEvents) item.Dispose(); }
            using var maxState = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"state-max\"", StringComparison.Ordinal)));
            Assert.Equal("max", maxState.RootElement.GetProperty("data").GetProperty("thinkingLevel").GetString());
            using var entries = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"thinking-entries\"", StringComparison.Ordinal)));
            Assert.Contains(entries.RootElement.GetProperty("data").GetProperty("entries").EnumerateArray(), entry =>
                entry.GetProperty("type").GetString() == "thinking_level_change" &&
                entry.GetProperty("thinkingLevel").GetString() == "max");
            using var cycledThinking = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"cycle-thinking\"", StringComparison.Ordinal)));
            Assert.Equal("off", cycledThinking.RootElement.GetProperty("data").GetProperty("level").GetString());
            using var offState = JsonDocument.Parse(Assert.Single(lines, line =>
                line.Contains("\"id\":\"state-off\"", StringComparison.Ordinal)));
            Assert.Equal("off", offState.RootElement.GetProperty("data").GetProperty("thinkingLevel").GetString());
            Assert.DoesNotContain(lines, line => line.Contains("\"type\":\"error\"", StringComparison.Ordinal));
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteCommandAsync(Process process, object command, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<List<string>> ReadResponsesAsync(Process process, IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var remaining = ids.ToHashSet(StringComparer.Ordinal);
        var lines = new List<string>();
        while (remaining.Count > 0)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new EndOfStreamException("RPC process closed stdout before returning all command responses.");
            lines.Add(line);
            using var record = JsonDocument.Parse(line);
            var root = record.RootElement;
            if (root.GetProperty("type").GetString() == "response" && root.TryGetProperty("id", out var id))
                remaining.Remove(id.GetString() ?? "");
        }
        return lines;
    }
}
