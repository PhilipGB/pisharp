using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class NativeExtensionModelProcessTests
{
    [Fact]
    public async Task PackedExternalWireProviderUsesHostBridgeAtRealHttpBoundaries()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var sessionDirectory = Path.Combine(fixture.Root, "wire-sessions");
        var tracePath = Path.Combine(fixture.Root, "native-wire-trace.log");
        Directory.CreateDirectory(sessionDirectory);
        var package = await ResourcePackageCommandProcessTests.PackExternalNativeModelFixtureAsync(
            Path.Combine(fixture.Root, "native-wire-package"));
        var installed = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", package);
        Assert.Equal(0, installed.ExitCode);

        using var listener = StartWireListener(out var port);
        string? body = null;
        string? wireHeader = null;
        string? removedHeader = "not-observed";
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync();
            wireHeader = request.Request.Headers["x-native-wire"];
            removedHeader = request.Request.Headers["x-native-remove"];
            using var reader = new StreamReader(request.Request.InputStream);
            body = await reader.ReadToEndAsync();
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            await writer.WriteAsync("data: {\"text\":\"bridge response\"}\n\n");
            await writer.FlushAsync();
            request.Response.Close();
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var process = StartProcess(fixture, sessionDirectory, tracePath, continueSession: false,
            wireEndpoint: $"http://127.0.0.1:{port}/wire");
        var errors = new ConcurrentQueue<string>();
        var stderr = CaptureStandardErrorAsync(process, errors);
        await SetModelAsync(process, "select-wire", "native_wire_fixture", "wire-model", timeout.Token);
        await NativeExtensionStateProcessTests.RunModelPromptAsync(process, "wire-turn", "call the wire fixture",
            timeout.Token);
        await server.WaitAsync(timeout.Token);
        await WaitForTraceAsync(tracePath, line => line ==
            "wire|stream|native_wire_fixture|pisharp-extension|message", timeout.Token);
        await StopProcessAsync(process, timeout.Token);
        await stderr.WaitAsync(timeout.Token);

        using var sent = JsonDocument.Parse(body!);
        Assert.Equal("call the wire fixture", sent.RootElement.GetProperty("prompt").GetString());
        Assert.True(sent.RootElement.GetProperty("hostTransformed").GetBoolean());
        Assert.Equal("host-transformed", wireHeader);
        Assert.Null(removedHeader);
        var wireEvents = (await File.ReadAllLinesAsync(tracePath, timeout.Token))
            .Where(line => line.StartsWith("wire|", StringComparison.Ordinal)).ToArray();
        Assert.Equal([
            "wire|payload|native_wire_fixture|pisharp-extension",
            "wire|headers|native_wire_fixture|pisharp-extension",
            "wire|response|native_wire_fixture|pisharp-extension|200",
            "wire|stream|native_wire_fixture|pisharp-extension|message"
        ], wireEvents);
        Assert.DoesNotContain("Unhandled exception", string.Join("\n", errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackedExtensionRegistersProviderMcpAndVirtualModelAcrossProcessesAndReplacement()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var sessionDirectory = Path.Combine(fixture.Root, "sessions-v1");
        var replacementSessionDirectory = Path.Combine(fixture.Root, "sessions-v2");
        var tracePath = Path.Combine(fixture.Root, "native-model-trace.log");
        Directory.CreateDirectory(sessionDirectory);
        Directory.CreateDirectory(replacementSessionDirectory);
        await WriteDefaultsAsync(fixture, "native_fixture_virtual", "auto");
        await WriteUnrelatedMcpAsync(fixture);

        var v1 = await ResourcePackageCommandProcessTests.PackExternalNativeModelFixtureAsync(
            Path.Combine(fixture.Root, "native-model-v1"));
        var v2 = await ResourcePackageCommandProcessTests.PackExternalNativeModelFixtureAsync(
            Path.Combine(fixture.Root, "native-model-v2"), revision: "v2", version: "2.0.0");
        var unrelatedPackage = await ResourcePackageCommandProcessTests.PackExternalNativeFixtureAsync(
            Path.Combine(fixture.Root, "unrelated-native-package"));
        var installed = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", v1);
        Assert.Equal(0, installed.ExitCode);
        var unrelatedInstalled = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", unrelatedPackage);
        Assert.Equal(0, unrelatedInstalled.ExitCode);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Process? first = null;
        Process? resumed = null;
        Process? replacement = null;
        Process? afterRemoval = null;
        ConcurrentQueue<string>? firstErrorLines = null;
        Task? firstErrors = null;
        try
        {
            first = StartProcess(fixture, sessionDirectory, tracePath, continueSession: false,
                sessionName: "startup event");
            firstErrorLines = new();
            firstErrors = CaptureStandardErrorAsync(first, firstErrorLines);
            JsonElement[] models;
            try { models = await GetModelsAsync(first, "models-v1", timeout.Token); }
            catch (EndOfStreamException error) when (first.HasExited)
            {
                await firstErrors.WaitAsync(timeout.Token);
                throw new InvalidOperationException("The initial RPC process failed before model discovery: " +
                    string.Join(" | ", firstErrorLines), error);
            }
            Assert.Contains(models, model => model.GetProperty("provider").GetString() == "native_fixture" &&
                model.GetProperty("id").GetString() == "physical-main");
            Assert.Contains(models, model => model.GetProperty("provider").GetString() == "native_fixture_virtual" &&
                model.GetProperty("id").GetString() == "auto");
            Assert.StartsWith("v1 command / private dependency ",
                await NativeExtensionStateProcessTests.RunExtensionCommandAsync(first, "unrelated-v1",
                    "/native-fixture", timeout.Token), StringComparison.Ordinal);
            using (var mcpProjection = JsonDocument.Parse(await NativeExtensionStateProcessTests.RunExtensionCommandAsync(
                       first, "mcp-projection-v1", "/native-model-fixture-tools", timeout.Token)))
            {
                var projectedTool = Assert.Single(mcpProjection.RootElement.EnumerateArray());
                Assert.Equal("mcp__native_fixture_mcp__echo", projectedTool.GetProperty("Name").GetString());
                Assert.Equal("extension", projectedTool.GetProperty("Source").GetString());
                Assert.Contains("PiSharp.TestExtensions.NativeModelFixture/1.0.0",
                    projectedTool.GetProperty("SourcePath").GetString(), StringComparison.OrdinalIgnoreCase);
                Assert.Equal("mcp__native_fixture_mcp", projectedTool.GetProperty("Namespace").GetString());
            }

            await SetModelAsync(first, "select-physical", "native_fixture", "physical-main", timeout.Token);
            var thinkingSelection = await SendRpcRequestAsync(first,
                new { id = "select-high-thinking", type = "set_thinking_level", level = "high" },
                "select-high-thinking", timeout.Token);
            Assert.True(thinkingSelection.GetProperty("success").GetBoolean(), thinkingSelection.ToString());
            await NativeExtensionStateProcessTests.RunModelPromptAsync(first, "mcp-turn",
                "Call the available native fixture tool", timeout.Token);
            var firstTrace = await WaitForTraceAsync(tracePath,
                line => line.StartsWith("request|physical-main|", StringComparison.Ordinal), timeout.Token);
            Assert.Contains("request|physical-main|", firstTrace, StringComparison.Ordinal);
            Assert.True(firstTrace.Contains("mcp__native_fixture_mcp__echo", StringComparison.Ordinal),
                $"MCP provider tools were not exposed. Trace: {firstTrace}. Startup diagnostics: {string.Join(" | ", firstErrorLines)}");
            Assert.True(firstTrace.Contains("mcp__unrelated__echo", StringComparison.Ordinal),
                $"Configured MCP tools were not exposed. Trace: {firstTrace}. Startup diagnostics: {string.Join(" | ", firstErrorLines)}");
            var completedTrace = await WaitForTraceAsync(tracePath,
                line => line.StartsWith("request|physical-main|", StringComparison.Ordinal) &&
                    line.Contains("tool-result:true", StringComparison.Ordinal), timeout.Token);
            Assert.Contains("system:true", completedTrace, StringComparison.Ordinal);

            await SetModelAsync(first, "select-virtual", "native_fixture_virtual", "auto", timeout.Token);
            await NativeExtensionStateProcessTests.RunModelPromptAsync(first, "virtual-turn-1",
                "Use the automatic fixture route", timeout.Token);
            Assert.Contains(await File.ReadAllLinesAsync(tracePath), line => line == "route|user|low|0");
            Assert.Equal("event lifecycle session", await NativeExtensionStateProcessTests.RunExtensionCommandAsync(
                first, "rename-session", "/native-model-fixture-rename event lifecycle session", timeout.Token));
            var compact = await SendRpcRequestAsync(first,
                new { id = "compact-session", type = "compact" }, "compact-session", timeout.Token);
            Assert.True(compact.GetProperty("success").GetBoolean(), compact.ToString());
            var firstEvents = await File.ReadAllLinesAsync(tracePath);
            Assert.Contains(firstEvents, line => line.StartsWith("event|v1|model-select|Set|", StringComparison.Ordinal));
            Assert.Contains(firstEvents, line => line == "event|v1|thinking-select|Low|High");
            Assert.Contains(firstEvents, line => line == "event|v1|session-info|startup event");
            Assert.Contains(firstEvents, line => line == "event|v1|session-info|event lifecycle session");
            Assert.Contains(firstEvents, line => line.StartsWith("event|v1|session-compact|Manual|", StringComparison.Ordinal));
            Assert.Contains(firstEvents, line => line.StartsWith("event|v1|agent-start|rpc|", StringComparison.Ordinal));
            Assert.Contains(firstEvents, line => line.StartsWith("event|v1|tool-start|native-fixture-mcp-call|", StringComparison.Ordinal));
            Assert.Contains(firstEvents, line => line.StartsWith("event|v1|tool-end|native-fixture-mcp-call|", StringComparison.Ordinal));
            Assert.Contains(firstEvents, line => line.StartsWith("event|v1|agent-end|Completed|", StringComparison.Ordinal));
            var firstAgentContext = firstEvents.First(line =>
                line.StartsWith("event|v1|agent-start|rpc|", StringComparison.Ordinal));
            var agentStart = Array.FindIndex(firstEvents, line => line.StartsWith("event|v1|agent-start|", StringComparison.Ordinal));
            var firstTurnStart = Array.FindIndex(firstEvents, line => line == "event|v1|turn-start|0");
            var toolStart = Array.FindIndex(firstEvents, line => line.StartsWith("event|v1|tool-start|native-fixture-mcp-call|", StringComparison.Ordinal));
            var toolEnd = Array.FindIndex(firstEvents, line => line.StartsWith("event|v1|tool-end|native-fixture-mcp-call|", StringComparison.Ordinal));
            var firstTurnEnd = Array.FindIndex(firstEvents, line => line.StartsWith("event|v1|turn-end|0|", StringComparison.Ordinal));
            var secondTurnStart = Array.FindIndex(firstEvents, line => line == "event|v1|turn-start|1");
            var agentEnd = Array.FindIndex(firstEvents, line => line.StartsWith("event|v1|agent-end|Completed|", StringComparison.Ordinal));
            Assert.True(agentStart < firstTurnStart && firstTurnStart < toolStart && toolStart < toolEnd &&
                toolEnd < firstTurnEnd && firstTurnEnd < secondTurnStart && secondTurnStart < agentEnd,
                "The external package observed an unexpected agent/turn/tool event order: " +
                string.Join(" | ", firstEvents.Where(line => line.StartsWith("event|v1|", StringComparison.Ordinal))));
            await StopProcessAsync(first, timeout.Token);
            Assert.DoesNotContain("Unhandled exception", string.Join("\n", firstErrorLines), StringComparison.Ordinal);
            first.Dispose();
            first = null;

            await RunJsonPromptAsync(fixture, tracePath, timeout.Token);
            var jsonEvents = await File.ReadAllLinesAsync(tracePath, timeout.Token);
            Assert.Contains(jsonEvents, line => line.StartsWith("event|v1|agent-start|json|", StringComparison.Ordinal));
            Assert.Contains(jsonEvents, line => line.StartsWith("event|v1|agent-end|Completed|", StringComparison.Ordinal));

            var saved = Assert.Single(Directory.GetFiles(sessionDirectory, "*.session.json"));
            var savedContent = await File.ReadAllTextAsync(saved);
            Assert.Contains("native_fixture_virtual", savedContent, StringComparison.Ordinal);
            Assert.Contains("routeCount", savedContent, StringComparison.Ordinal);
            Assert.Contains("native_event_observed", savedContent, StringComparison.Ordinal);
            using (var savedDocument = JsonDocument.Parse(savedContent))
            {
                Assert.Equal("auto", savedDocument.RootElement.GetProperty("Model").GetString());
                Assert.Equal("native_fixture_virtual", savedDocument.RootElement.GetProperty("Provider").GetString());
            }

            resumed = StartProcess(fixture, sessionDirectory, tracePath, continueSession: true);
            var resumedTraceOffset = (await File.ReadAllLinesAsync(tracePath, timeout.Token)).Length;
            var resumedErrorLines = new ConcurrentQueue<string>();
            var resumedErrors = CaptureStandardErrorAsync(resumed, resumedErrorLines);
            await NativeExtensionStateProcessTests.RunModelPromptAsync(resumed, "virtual-turn-2",
                "Continue the automatic fixture route", timeout.Token);
            Assert.Contains(await WaitForTraceAsync(tracePath, line => line == "route|user|low|1", timeout.Token),
                "route|user|low|1", StringComparison.Ordinal);
            var resumedEvents = (await File.ReadAllLinesAsync(tracePath, timeout.Token))
                .Skip(resumedTraceOffset).ToArray();
            var resumedAgentContext = Assert.Single(resumedEvents, line =>
                line.StartsWith("event|v1|agent-start|rpc|", StringComparison.Ordinal));
            Assert.Equal(firstAgentContext.Split('|')[4], resumedAgentContext.Split('|')[4]);
            Assert.Contains("|native_fixture_virtual/auto|Low|", resumedAgentContext, StringComparison.Ordinal);
            await StopProcessAsync(resumed, timeout.Token);
            await resumedErrors.WaitAsync(timeout.Token);
            Assert.DoesNotContain("Unhandled exception", string.Join("\n", resumedErrorLines), StringComparison.Ordinal);
            resumed.Dispose();
            resumed = null;
            savedContent = await File.ReadAllTextAsync(saved);

            var updated = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "update", "--extension", v2);
            Assert.Equal(0, updated.ExitCode);
            Assert.Contains("PiSharp.TestExtensions.NativeModelFixture 2.0.0", updated.Output, StringComparison.Ordinal);
            await WriteDefaultsAsync(fixture, "native_fixture_virtual", "auto-v2");
            var replacementTraceOffset = (await File.ReadAllLinesAsync(tracePath, timeout.Token)).Length;
            replacement = StartProcess(fixture, replacementSessionDirectory, tracePath, continueSession: false);
            var replacementErrorLines = new ConcurrentQueue<string>();
            var replacementErrors = CaptureStandardErrorAsync(replacement, replacementErrorLines);
            JsonElement[] replacementModels;
            try { replacementModels = await GetModelsAsync(replacement, "models-v2", timeout.Token); }
            catch (EndOfStreamException error)
            {
                if (!replacement.HasExited)
                    await replacement.WaitForExitAsync(timeout.Token);
                await replacementErrors.WaitAsync(timeout.Token);
                throw new InvalidOperationException("The replacement RPC process failed before model discovery: " +
                    string.Join(" | ", replacementErrorLines), error);
            }
            Assert.Contains(replacementModels, model => model.GetProperty("provider").GetString() == "native_fixture" &&
                model.GetProperty("id").GetString() == "physical-main-v2");
            Assert.DoesNotContain(replacementModels, model => model.GetProperty("provider").GetString() == "native_fixture" &&
                model.GetProperty("id").GetString() == "physical-main");
            Assert.Contains(replacementModels, model => model.GetProperty("provider").GetString() == "native_fixture_virtual" &&
                model.GetProperty("id").GetString() == "auto-v2");
            Assert.DoesNotContain(replacementModels, model => model.GetProperty("provider").GetString() == "native_fixture_virtual" &&
                model.GetProperty("id").GetString() == "auto");
            Assert.StartsWith("v1 command / private dependency ",
                await NativeExtensionStateProcessTests.RunExtensionCommandAsync(replacement, "unrelated-v2",
                    "/native-fixture", timeout.Token), StringComparison.Ordinal);
            using (var mcpProjection = JsonDocument.Parse(await NativeExtensionStateProcessTests.RunExtensionCommandAsync(
                       replacement, "mcp-projection-v2", "/native-model-fixture-tools", timeout.Token)))
            {
                var projectedTool = Assert.Single(mcpProjection.RootElement.EnumerateArray());
                Assert.Equal("mcp__native_fixture_mcp_v2__echo_v2", projectedTool.GetProperty("Name").GetString());
                Assert.Equal("extension", projectedTool.GetProperty("Source").GetString());
                Assert.Contains("PiSharp.TestExtensions.NativeModelFixture/2.0.0",
                    projectedTool.GetProperty("SourcePath").GetString(), StringComparison.OrdinalIgnoreCase);
                Assert.Equal("mcp__native_fixture_mcp_v2", projectedTool.GetProperty("Namespace").GetString());
            }

            await SetModelAsync(replacement, "select-physical-v2", "native_fixture", "physical-main-v2", timeout.Token);
            await NativeExtensionStateProcessTests.RunModelPromptAsync(replacement, "mcp-turn-v2",
                "Call the replacement native fixture tool", timeout.Token);
            var replacementEvents = await File.ReadAllLinesAsync(tracePath);
            Assert.Contains(replacementEvents, line => line.StartsWith("event|v2|model-select|Set|", StringComparison.Ordinal));
            Assert.Contains(replacementEvents, line => line.StartsWith("event|v2|agent-start|rpc|", StringComparison.Ordinal));
            var replacementRunEvents = replacementEvents.Skip(replacementTraceOffset).ToArray();
            Assert.DoesNotContain(replacementRunEvents, line => line.StartsWith("event|v1|", StringComparison.Ordinal));
            Assert.Single(replacementRunEvents, line => line.StartsWith("event|v2|agent-start|rpc|", StringComparison.Ordinal));
            Assert.Single(replacementRunEvents, line => line.StartsWith("event|v2|model-select|Set|", StringComparison.Ordinal));
            var replacementTrace = await WaitForTraceAsync(tracePath,
                line => line.StartsWith("request|physical-main-v2|", StringComparison.Ordinal) &&
                    line.Contains("mcp__native_fixture_mcp_v2__echo_v2", StringComparison.Ordinal), timeout.Token);
            Assert.Contains("mcp__unrelated__echo", replacementTrace, StringComparison.Ordinal);
            Assert.DoesNotContain("mcp__native_fixture_mcp__echo", replacementTrace, StringComparison.Ordinal);
            await StopProcessAsync(replacement, timeout.Token);
            await replacementErrors.WaitAsync(timeout.Token);
            Assert.DoesNotContain("Unhandled exception", string.Join("\n", replacementErrorLines), StringComparison.Ordinal);
            replacement.Dispose();
            replacement = null;

            var v3 = await ResourcePackageCommandProcessTests.PackExternalNativeModelFixtureAsync(
                Path.Combine(fixture.Root, "native-model-v3"), revision: "v3", version: "3.0.0");
            var brokenUpdate = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "update", "--extension", v3);
            Assert.Equal(0, brokenUpdate.ExitCode);
            await WriteDefaultsAsync(fixture, "openai", "gpt-4.1");
            var failedConfiguration = await NativeExtensionStateProcessTests.RunCliAsync(fixture,
                "--offline", "--list-models");
            Assert.Equal(0, failedConfiguration.ExitCode);
            Assert.Contains("native fixture Configure rollback probe", failedConfiguration.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("native_fixture", failedConfiguration.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("native_fixture_virtual", failedConfiguration.Output, StringComparison.Ordinal);

            var nativePackages = new NativeExtensionPackageStore(fixture.AgentDirectory);
            var failedSource = await nativePackages.InspectAsync(fixture.ProjectDirectory, v3, timeout.Token);
            var failedRoots = await nativePackages.ResolveAsync(fixture.ProjectDirectory, trusted: true,
                userSources: [failedSource]);
            using var rollbackCatalog = ExtensionCatalog.Load(fixture.AgentDirectory, fixture.ProjectDirectory,
                projectTrusted: true, nativeExtensionPackageRoots: failedRoots);
            Assert.Contains(rollbackCatalog.Warnings,
                warning => warning.Warning.Contains("Configure rollback probe", StringComparison.Ordinal));
            Assert.Empty(rollbackCatalog.Registration.SessionBeforeSwitchHandlers);
            Assert.Empty(rollbackCatalog.Registration.SessionBeforeForkHandlers);
            Assert.Empty(rollbackCatalog.Registration.SessionBeforeTreeHandlers);
            Assert.Empty(rollbackCatalog.Registration.SessionTreeHandlers);
            Assert.Empty(rollbackCatalog.Registration.SessionBeforeCompactHandlers);

            var removed = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "remove",
                "PiSharp.TestExtensions.NativeModelFixture");
            Assert.Equal(0, removed.ExitCode);
            Assert.Equal(savedContent, await File.ReadAllTextAsync(saved));
            using (var unrelatedMcp = JsonDocument.Parse(await File.ReadAllTextAsync(
                       Path.Combine(fixture.AgentDirectory, "mcp.json"))))
                Assert.True(unrelatedMcp.RootElement.GetProperty("mcpServers").TryGetProperty("unrelated", out _));
            await WriteDefaultsAsync(fixture, "openai", "gpt-4.1");
            var catalog = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "--offline", "--list-models");
            Assert.Equal(0, catalog.ExitCode);
            Assert.DoesNotContain("native_fixture", catalog.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("native_fixture_virtual", catalog.Output, StringComparison.Ordinal);

            afterRemoval = StartProcess(fixture, replacementSessionDirectory, tracePath, continueSession: false);
            var afterRemovalErrors = new ConcurrentQueue<string>();
            var afterRemovalErrorReader = CaptureStandardErrorAsync(afterRemoval, afterRemovalErrors);
            Assert.StartsWith("v1 command / private dependency ",
                await NativeExtensionStateProcessTests.RunExtensionCommandAsync(afterRemoval,
                    "unrelated-after-remove", "/native-fixture", timeout.Token), StringComparison.Ordinal);
            using (var controls = JsonDocument.Parse(await NativeExtensionStateProcessTests.RunExtensionCommandAsync(
                       afterRemoval, "mcp-after-remove", "/native-fixture-controls", timeout.Token)))
            {
                var toolNames = controls.RootElement.GetProperty("configuredTools").EnumerateArray()
                    .Select(tool => tool.GetProperty("Name").GetString()).ToArray();
                Assert.Contains("mcp__unrelated__echo", toolNames);
                Assert.DoesNotContain("mcp__native_fixture_mcp__echo", toolNames);
                Assert.DoesNotContain("mcp__native_fixture_mcp_v2__echo_v2", toolNames);
            }
            var removedModelSelection = await SendRpcRequestAsync(afterRemoval,
                new
                {
                    id = "select-removed-model",
                    type = "set_model",
                    provider = "native_fixture",
                    modelId = "physical-main"
                }, "select-removed-model", timeout.Token);
            Assert.False(removedModelSelection.GetProperty("success").GetBoolean());
            Assert.Contains("Unknown provider 'native_fixture'",
                removedModelSelection.GetProperty("error").GetString(), StringComparison.Ordinal);
            await StopProcessAsync(afterRemoval, timeout.Token);
            await afterRemovalErrorReader.WaitAsync(timeout.Token);
            Assert.DoesNotContain("Unhandled exception", string.Join("\n", afterRemovalErrors),
                StringComparison.Ordinal);
            afterRemoval.Dispose();
            afterRemoval = null;
        }
        finally
        {
            foreach (var process in new[] { first, resumed, replacement, afterRemoval })
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                process?.Dispose();
            }
        }
    }

    private static async Task WriteDefaultsAsync(ManagedNpmFixture fixture, string provider, string model)
    {
        var path = Path.Combine(fixture.AgentDirectory, "settings.json");
        var settings = File.Exists(path)
            ? JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject ??
              throw new InvalidDataException("The fixture settings file must contain an object.")
            : new JsonObject();
        settings["defaultProvider"] = provider;
        settings["defaultModel"] = model;
        settings["defaultThinkingLevel"] = "low";
        settings["compaction"] = new JsonObject { ["enabled"] = false, ["keepRecentTokens"] = 1 };
        await File.WriteAllTextAsync(path, settings.ToJsonString());
    }

    private static async Task RunJsonPromptAsync(ManagedNpmFixture fixture, string tracePath,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in new[] { "--mode", "json", "--offline", "--provider", "native_fixture", "--model",
                     "physical-main", "--no-tools", "--no-session", "external JSON callback" })
            start.ArgumentList.Add(argument);
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                     "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_NATIVE_MODEL_TRACE" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(fixture.Root, "sessions-json");
        start.Environment["PISHARP_NATIVE_MODEL_TRACE"] = tracePath;
        using var process = Process.Start(start) ?? throw new IOException("Could not start native extension JSON process.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Assert.True(process.ExitCode == 0,
            $"JSON process failed. stdout={await output}; stderr={await error}");
    }

    private static async Task WriteUnrelatedMcpAsync(ManagedNpmFixture fixture)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestInfrastructure", "Fixtures", "mcp_fixture.py");
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "mcp.json"), JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["unrelated"] = new
                {
                    command = "python3",
                    args = new[] { fixturePath },
                    enabled = true,
                    exposure = "direct"
                }
            }
        }));
    }

    private static Process StartProcess(ManagedNpmFixture fixture, string sessionDirectory, string tracePath,
        bool continueSession, string? sessionName = null, string? wireEndpoint = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in new[] { "--mode", "rpc", "--offline", "--session-dir", sessionDirectory })
            start.ArgumentList.Add(argument);
        if (continueSession) start.ArgumentList.Add("--continue");
        if (sessionName is not null)
        {
            start.ArgumentList.Add("--name");
            start.ArgumentList.Add(sessionName);
        }
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                     "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_NATIVE_MODEL_TRACE" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;
        start.Environment["PISHARP_NATIVE_MODEL_TRACE"] = tracePath;
        start.Environment.Remove("PISHARP_NATIVE_WIRE_ENDPOINT");
        if (wireEndpoint is not null) start.Environment["PISHARP_NATIVE_WIRE_ENDPOINT"] = wireEndpoint;
        return Process.Start(start) ?? throw new IOException("Could not start native extension model RPC process.");
    }

    private static HttpListener StartWireListener(out int port)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }

    private static async Task<JsonElement[]> GetModelsAsync(Process process, string id,
        CancellationToken cancellationToken)
    {
        var response = await SendRpcRequestAsync(process, new { id, type = "get_available_models" }, id,
            cancellationToken);
        Assert.True(response.GetProperty("success").GetBoolean());
        return response.GetProperty("data").GetProperty("models").EnumerateArray()
            .Select(model => model.Clone()).ToArray();
    }

    private static async Task SetModelAsync(Process process, string id, string provider, string model,
        CancellationToken cancellationToken)
    {
        var response = await SendRpcRequestAsync(process, new { id, type = "set_model", provider, modelId = model },
            id, cancellationToken);
        Assert.True(response.GetProperty("success").GetBoolean(), response.ToString());
    }

    private static async Task<JsonElement> SendRpcRequestAsync(Process process, object request, string id,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            if (record.TryGetProperty("id", out var responseId) && responseId.ToString() == id &&
                record.TryGetProperty("type", out var type) && type.GetString() == "response")
                return record.Clone();
            if (record.TryGetProperty("type", out type) && type.GetString() == "extension_error")
                throw new InvalidOperationException(record.GetProperty("error").GetString());
        }
        throw new EndOfStreamException($"RPC process closed before response {id}.");
    }

    private static async Task<string> WaitForTraceAsync(string path, Func<string, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                var lines = await File.ReadAllLinesAsync(path, cancellationToken);
                if (lines.FirstOrDefault(predicate) is { } match) return match;
            }
            await Task.Delay(20, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new UnreachableException();
    }

    private static async Task StopProcessAsync(Process process, CancellationToken cancellationToken)
    {
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken);
        Assert.Equal(0, process.ExitCode);
    }

    private static async Task CaptureStandardErrorAsync(Process process, ConcurrentQueue<string> lines)
    {
        while (await process.StandardError.ReadLineAsync() is { } line) lines.Enqueue(line);
    }
}
