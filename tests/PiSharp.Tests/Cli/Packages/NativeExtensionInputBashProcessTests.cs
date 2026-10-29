using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class NativeExtensionInputBashProcessTests
{
    private sealed class PtyOutputCapture
    {
        private readonly object _gate = new();
        private readonly StringBuilder _output = new();
        private readonly List<(string Text, int StartIndex, TaskCompletionSource Completion)> _waiters = [];
        private readonly TaskCompletionSource _inputReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task<string> _completion;

        public PtyOutputCapture(Process process) => _completion = ReadAsync(process);

        public Task InputReady => _inputReady.Task;
        public Task<string> Completion => _completion;
        public int Mark { get { lock (_gate) return _output.Length; } }
        public string CapturedOutput { get { lock (_gate) return _output.ToString(); } }

        public async Task WaitForTextAsync(string text, int startIndex, CancellationToken cancellationToken)
        {
            Task wait;
            lock (_gate)
            {
                var captured = _output.ToString();
                if (captured.IndexOf(text, Math.Min(startIndex, captured.Length), StringComparison.Ordinal) >= 0)
                    return;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((text, startIndex, completion));
                wait = completion.Task;
            }
            await wait.WaitAsync(cancellationToken);
        }

        public async Task WaitForIdleFrameAfterTextAsync(string text, int startIndex,
            CancellationToken cancellationToken)
        {
            await WaitForTextAsync(text, startIndex, cancellationToken);
            const string activeFooter = "Enter steers · follow-up queues · Escape aborts";
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frames = TerminalOutputFrameReader.Read(CapturedOutput, rows: 24, columns: 80);
                if (frames.Count > 0)
                {
                    var frame = frames[^1].Screen;
                    if (frame.Contains(text, StringComparison.Ordinal) &&
                        !frame.Contains(activeFooter, StringComparison.Ordinal) &&
                        !frame.Contains("Working", StringComparison.Ordinal))
                        return;
                }
                await Task.Delay(25, cancellationToken);
            }
        }

        private async Task<string> ReadAsync(Process process)
        {
            var buffer = new char[2048];
            int count;
            while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0)
            {
                lock (_gate)
                {
                    _output.Append(buffer, 0, count);
                    var captured = _output.ToString();
                    if (captured.Contains("\u001b[?2004h", StringComparison.Ordinal)) _inputReady.TrySetResult();
                    for (var index = _waiters.Count - 1; index >= 0; index--)
                    {
                        var waiter = _waiters[index];
                        if (captured.IndexOf(waiter.Text, Math.Min(waiter.StartIndex, captured.Length),
                                StringComparison.Ordinal) < 0)
                            continue;
                        _waiters.RemoveAt(index);
                        waiter.Completion.TrySetResult();
                    }
                }
            }
            lock (_gate) return _output.ToString();
        }
    }

    [Fact]
    public async Task ExternalPackageInterceptsRpcInputAndReplacesUserBashInFreshProcess()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var package = await PackAsync(Path.Combine(fixture.Root, "package"));
        using (var archive = ZipFile.OpenRead(package))
        {
            var specEntry = Assert.Single(archive.Entries,
                entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            using var reader = new StreamReader(specEntry.Open());
            var nuspec = await reader.ReadToEndAsync();
            Assert.Contains("PiSharp.Extensions.Abstractions", nuspec);
            Assert.DoesNotContain("PiSharp.Runtime", nuspec);
            Assert.DoesNotContain("PiSharp.Cli", nuspec);
            Assert.DoesNotContain("lib/net10.0/PiSharp.Extensions.Abstractions.dll", archive.Entries
                .Select(entry => entry.FullName));
        }

        var install = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", package);
        Assert.True(install.ExitCode == 0, install.Error + "\n" + install.Output);

        var sessions = Path.Combine(fixture.Root, "sessions");
        Directory.CreateDirectory(sessions);
        var logPath = Path.Combine(fixture.Root, "extension.log");
        var fallbackPath = Path.Combine(fixture.Root, "shell-fallback-ran");
        var command = "touch " + ProcessTestHelpers.ShellQuote(fallbackPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var process = Start(fixture, sessions, logPath);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await WriteAsync(process, new
            {
                id = "handled-input",
                type = "prompt",
                message = "native-package-handled"
            }, timeout.Token);
            var inputResponse = await ReadResponseAsync(process, "handled-input", timeout.Token);
            Assert.True(inputResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("handled", inputResponse.RootElement.GetProperty("data")
                .GetProperty("disposition").GetString());

            await WriteAsync(process, new
            {
                id = "recoverable-input-error",
                type = "prompt",
                message = "native-package-input-error"
            }, timeout.Token);
            using var inputErrorResponse = await ReadResponseAsync(process, "recoverable-input-error", timeout.Token);
            Assert.False(inputErrorResponse.RootElement.GetProperty("success").GetBoolean());

            await WriteAsync(process, new
            {
                id = "replaced-bash",
                type = "bash",
                command,
                excludeFromContext = true
            }, timeout.Token);
            using var bashResponse = await ReadResponseAsync(process, "replaced-bash", timeout.Token);
            Assert.True(bashResponse.RootElement.GetProperty("success").GetBoolean());
            var result = bashResponse.RootElement.GetProperty("data");
            Assert.Equal("external-package-bash-result", result.GetProperty("output").GetString());
            Assert.Equal(17, result.GetProperty("exitCode").GetInt32());
            Assert.True(result.GetProperty("truncated").GetBoolean());

            await WriteAsync(process, new
            {
                id = "custom-bash",
                type = "bash",
                command = "remote status"
            }, timeout.Token);
            using var customResponse = await ReadResponseAsync(process, "custom-bash", timeout.Token);
            Assert.True(customResponse.RootElement.GetProperty("success").GetBoolean());
            var customResult = customResponse.RootElement.GetProperty("data");
            Assert.Equal($"custom-start\n{fixture.ProjectDirectory}:remote status\n",
                customResult.GetProperty("output").GetString());
            Assert.Equal(23, customResult.GetProperty("exitCode").GetInt32());

            var cancelledMarker = Path.Combine(fixture.Root, "cancelled-bash-shell-ran");
            await WriteAsync(process, new
            {
                id = "cancelled-bash",
                type = "bash",
                command = "cancel-result " + ProcessTestHelpers.ShellQuote(cancelledMarker)
            }, timeout.Token);
            using var cancelledResponse = await ReadResponseAsync(process, "cancelled-bash", timeout.Token);
            Assert.True(cancelledResponse.RootElement.GetProperty("success").GetBoolean());
            var cancelledResult = cancelledResponse.RootElement.GetProperty("data");
            Assert.Equal("external-package-cancelled-result", cancelledResult.GetProperty("output").GetString());
            Assert.True(cancelledResult.GetProperty("cancelled").GetBoolean());
            Assert.False(cancelledResult.GetProperty("truncated").GetBoolean());
            Assert.Equal("external-full-output.log", cancelledResult.GetProperty("fullOutputPath").GetString());
            Assert.False(cancelledResult.TryGetProperty("exitCode", out _));

            var errorMarker = Path.Combine(fixture.Root, "bash-error-shell-ran");
            await WriteAsync(process, new
            {
                id = "failed-bash-handler",
                type = "bash",
                command = "throw " + ProcessTestHelpers.ShellQuote(errorMarker)
            }, timeout.Token);
            using var bashErrorResponse = await ReadResponseAsync(process, "failed-bash-handler", timeout.Token);
            Assert.False(bashErrorResponse.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("user_bash", bashErrorResponse.RootElement.GetProperty("error").GetString(),
                StringComparison.Ordinal);

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await errors.WaitAsync(timeout.Token));
        }
        finally
        {
            await StopAsync(process);
        }

        Assert.False(File.Exists(fallbackPath));
        var records = (await File.ReadAllLinesAsync(logPath, timeout.Token))
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var inputs = records.Where(record =>
                record.RootElement.GetProperty("type").GetString() == "input").ToArray();
            Assert.Equal(["first", "second", "first", "second"], inputs.Select(record =>
                record.RootElement.GetProperty("handler").GetString()));
            Assert.Equal(["native-package-handled", "native-package-handled", "native-package-input-error",
                "native-package-input-error"], inputs.Select(record =>
                record.RootElement.GetProperty("text").GetString()));
            Assert.All(inputs, input =>
                Assert.Equal("Rpc", input.RootElement.GetProperty("source").GetString()));

            var bashes = records.Where(record =>
                record.RootElement.GetProperty("type").GetString() == "user_bash").ToArray();
            Assert.Equal(4, bashes.Length);
            using var bash = bashes[0];
            Assert.Equal(command, bash.RootElement.GetProperty("command").GetString());
            Assert.Equal(fixture.ProjectDirectory, bash.RootElement.GetProperty("cwd").GetString());
            Assert.True(bash.RootElement.GetProperty("excludeFromContext").GetBoolean());
            using var customBash = bashes[1];
            Assert.Equal("remote status", customBash.RootElement.GetProperty("command").GetString());
            Assert.Equal(fixture.ProjectDirectory, customBash.RootElement.GetProperty("cwd").GetString());
            Assert.False(customBash.RootElement.GetProperty("excludeFromContext").GetBoolean());
            using var cancelledBash = bashes[2];
            Assert.StartsWith("cancel-result ", cancelledBash.RootElement.GetProperty("command").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(fixture.ProjectDirectory, cancelledBash.RootElement.GetProperty("cwd").GetString());
            using var failedBash = bashes[3];
            Assert.StartsWith("throw ", failedBash.RootElement.GetProperty("command").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            foreach (var record in records) record.Dispose();
        }

        var sessionPath = Assert.Single(Directory.GetFiles(sessions, "*.json"));
        var store = new ConversationStore(fixture.ProjectDirectory, sessions);
        var session = await store.LoadAsync(sessionPath, timeout.Token);
        var entries = session.Tree.ActivePath().Where(item => item.Type == "bash_execution").ToArray();
        Assert.Equal(3, entries.Length);
        Assert.Equal("external-package-bash-result", entries[0].Payload.GetProperty("output").GetString());
        Assert.True(entries[0].Payload.GetProperty("excludeFromContext").GetBoolean());
        Assert.Equal($"custom-start\n{fixture.ProjectDirectory}:remote status\n",
            entries[1].Payload.GetProperty("output").GetString());
        Assert.False(entries[1].Payload.GetProperty("excludeFromContext").GetBoolean());
        Assert.Equal("external-package-cancelled-result", entries[2].Payload.GetProperty("output").GetString());
        Assert.True(entries[2].Payload.GetProperty("cancelled").GetBoolean());
        Assert.False(entries[2].Payload.GetProperty("truncated").GetBoolean());
        Assert.Equal("external-full-output.log", entries[2].Payload.GetProperty("fullOutputPath").GetString());
        Assert.False(File.Exists(Path.Combine(fixture.Root, "cancelled-bash-shell-ran")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "bash-error-shell-ran")));
    }

    [Fact]
    public async Task ExternalPackageExtensionMessagesAreInterceptedWhenIdleAndDuringAnActiveRun()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var package = await PackAsync(Path.Combine(fixture.Root, "package"));
        var install = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", package);
        Assert.True(install.ExitCode == 0, install.Error + "\n" + install.Output);

        Directory.CreateDirectory(fixture.AgentDirectory);
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var models = """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:PORT/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-model","api":"openai-responses"}]}}}
            """.Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "models.json"), models);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var idleRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var extensionRequest = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(HttpListenerContext Context, string Body)> ReadProviderRequestAsync()
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            Assert.Equal("/v1/responses", context.Request.Url?.AbsolutePath);
            using var reader = new StreamReader(context.Request.InputStream);
            return (context, await reader.ReadToEndAsync(timeout.Token));
        }

        async Task RespondAsync(HttpListenerContext context, string responseId)
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.KeepAlive = false;
            await using var writer = new StreamWriter(context.Response.OutputStream);
            await writer.WriteAsync($"data: {{\"type\":\"response.created\",\"response\":{{\"id\":\"{responseId}\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}}}\n\n");
            await writer.WriteAsync($"data: {{\"type\":\"response.output_text.delta\",\"item_id\":\"msg_{responseId}\",\"output_index\":0,\"content_index\":0,\"delta\":\"{responseId}_OK\"}}\n\n");
            await writer.WriteAsync($"data: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"{responseId}\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}}}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            context.Response.Close();
        }

        var provider = Task.Run(async () =>
        {
            var (idleContext, idleBody) = await ReadProviderRequestAsync();
            idleRequest.TrySetResult(idleBody);
            await RespondAsync(idleContext, "native_idle");

            var (activeContext, blockerBody) = await ReadProviderRequestAsync();
            activeRequest.TrySetResult(blockerBody);
            await releaseBlocker.Task.WaitAsync(timeout.Token);
            await RespondAsync(activeContext, "native_blocker");

            var (extensionContext, extensionBody) = await ReadProviderRequestAsync();
            extensionRequest.TrySetResult(extensionBody);
            await RespondAsync(extensionContext, "native_extension_active");
        }, timeout.Token);

        var sessions = Path.Combine(fixture.Root, "sessions");
        Directory.CreateDirectory(sessions);
        var logPath = Path.Combine(fixture.Root, "extension-sources.log");
        using var process = Start(fixture, sessions, logPath, providerConfigured: true);
        var rpcOutput = new JsonLineProcessOutput(process);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await WriteAsync(process, new { id = "extension-idle", type = "prompt", message = "/native-send idle" },
                timeout.Token);
            using (var idleResponse = await rpcOutput.ReadResponseAsync("extension-idle", timeout.Token))
                Assert.True(idleResponse.RootElement.GetProperty("success").GetBoolean());
            var idleBody = await idleRequest.Task.WaitAsync(timeout.Token);
            Assert.Contains("native-package-extension-idle-final", idleBody, StringComparison.Ordinal);
            Assert.DoesNotContain("native-package-extension-idle-stage-one", idleBody, StringComparison.Ordinal);

            await WriteAsync(process, new { id = "blocker", type = "prompt", message = "native-package-blocker" },
                timeout.Token);
            using (var blockerResponse = await rpcOutput.ReadResponseAsync("blocker", timeout.Token))
                Assert.True(blockerResponse.RootElement.GetProperty("success").GetBoolean());
            var blockerBody = await activeRequest.Task.WaitAsync(timeout.Token);
            Assert.Contains("native-package-blocker", blockerBody, StringComparison.Ordinal);

            await WriteAsync(process, new { id = "extension-active", type = "prompt", message = "/native-send active" },
                timeout.Token);
            using (var extensionCommandResponse = await rpcOutput.ReadResponseAsync("extension-active", timeout.Token))
                Assert.True(extensionCommandResponse.RootElement.GetProperty("success").GetBoolean());
            releaseBlocker.TrySetResult();

            var activeBody = await extensionRequest.Task.WaitAsync(timeout.Token);
            Assert.Contains("native-package-extension-active-final", activeBody, StringComparison.Ordinal);
            Assert.DoesNotContain("native-package-extension-active-stage-one", activeBody, StringComparison.Ordinal);
            await provider.WaitAsync(timeout.Token);

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await rpcOutput.Completion.WaitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await errors.WaitAsync(timeout.Token));
        }
        finally
        {
            releaseBlocker.TrySetResult();
            await StopAsync(process);
            await rpcOutput.Completion;
        }

        var records = (await File.ReadAllLinesAsync(logPath, timeout.Token))
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var inputs = records.Where(record => record.RootElement.GetProperty("type").GetString() == "input")
                .ToArray();
            Assert.Equal(["first", "second", "first", "second", "first", "second"], inputs.Select(record =>
                record.RootElement.GetProperty("handler").GetString()));
            Assert.Equal(["native-package-extension-idle", "native-package-extension-idle-stage-one",
                "native-package-blocker", "native-package-blocker", "native-package-extension-active",
                "native-package-extension-active-stage-one"],
                inputs.Select(record => record.RootElement.GetProperty("text").GetString()));
            Assert.Equal(["Extension", "Extension", "Rpc", "Rpc", "Extension", "Extension"], inputs.Select(record =>
                record.RootElement.GetProperty("source").GetString()));
            Assert.Equal([null, null, null, null, "Steer", "Steer"], inputs.Select(record =>
                record.RootElement.GetProperty("behavior").GetString()));
        }
        finally
        {
            foreach (var record in records) record.Dispose();
        }
    }

    [Fact]
    public async Task ExternalPackageConfigureFailureRollsBackInputAndUserBashHandlers()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var package = await PackAsync(Path.Combine(fixture.Root, "package"));
        var install = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", package);
        Assert.True(install.ExitCode == 0, install.Error + "\n" + install.Output);

        var sessions = Path.Combine(fixture.Root, "sessions");
        Directory.CreateDirectory(sessions);
        var logPath = Path.Combine(fixture.Root, "configure-rollback.log");
        var shellMarker = Path.Combine(fixture.Root, "rollback-shell-ran");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = Start(fixture, sessions, logPath, failConfigure: true);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await WriteAsync(process, new { id = "after-rollback-input", type = "prompt", message = "ordinary input" },
                timeout.Token);
            using (var inputResponse = await ReadResponseAsync(process, "after-rollback-input", timeout.Token))
            {
                Assert.False(inputResponse.RootElement.GetProperty("success").GetBoolean());
                Assert.DoesNotContain("handled", inputResponse.RootElement.GetRawText(), StringComparison.Ordinal);
            }

            await WriteAsync(process, new
            {
                id = "after-rollback-bash",
                type = "bash",
                command = "touch " + ProcessTestHelpers.ShellQuote(shellMarker)
            }, timeout.Token);
            using (var bashResponse = await ReadResponseAsync(process, "after-rollback-bash", timeout.Token))
                Assert.True(bashResponse.RootElement.GetProperty("success").GetBoolean());

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("native input-bash fixture Configure failure", await errors.WaitAsync(timeout.Token),
                StringComparison.Ordinal);
        }
        finally { await StopAsync(process); }

        Assert.True(File.Exists(shellMarker));
        using var configureRecord = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(logPath,
            timeout.Token)));
        Assert.Equal("configure_failure", configureRecord.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ExternalPackageHandlesAndTransformsInteractiveInputBeforeProviderThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;

        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var package = await PackAsync(Path.Combine(fixture.Root, "package"));
        var install = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", package);
        Assert.True(install.ExitCode == 0, install.Error + "\n" + install.Output);

        Directory.CreateDirectory(fixture.AgentDirectory);
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var requestBodyTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            Assert.Equal("/v1/responses", context.Request.Url?.AbsolutePath);
            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync(timeout.Token);
            context.Response.ContentType = "text/event-stream";
            context.Response.KeepAlive = false;
            await using var writer = new StreamWriter(context.Response.OutputStream);
            await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_input\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_input\",\"output_index\":0,\"content_index\":0,\"delta\":\"NATIVE_INPUT_PROVIDER_OK\"}\n\n");
            await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_input\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}\n\n");
            await writer.WriteAsync("data: [DONE]\n\n");
            await writer.FlushAsync();
            context.Response.Close();
            return body;
        }, timeout.Token);

        var logPath = Path.Combine(fixture.Root, "interactive-input.log");
        var models = """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:PORT/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-model","api":"openai-responses"}]}}}
            """.Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "models.json"), models);
        var start = new ProcessStartInfo("/usr/bin/script")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"stty rows 24 cols 80; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --provider fixture --model fixture-model --no-session --offline --no-tools");
        start.ArgumentList.Add("/dev/null");
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(fixture.Root, "sessions");
        start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-key";
        start.Environment["PISHARP_NATIVE_INPUT_BASH_LOG"] = logPath;
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                     "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
            start.Environment.Remove(name);

        using var process = Process.Start(start) ?? throw new IOException("Could not start the interactive process.");
        var output = new PtyOutputCapture(process);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await output.InputReady.WaitAsync(timeout.Token);
            var handledMark = output.Mark;
            await process.StandardInput.WriteAsync("native-package-handled\r");
            await process.StandardInput.FlushAsync();
            await output.WaitForTextAsync("native-package-handled-notification", handledMark, timeout.Token);

            var transformedMark = output.Mark;
            await process.StandardInput.WriteAsync("native-package-transform\r");
            await process.StandardInput.FlushAsync();
            var requestBody = await requestBodyTask.WaitAsync(timeout.Token);
            Assert.Contains("native-package-final", requestBody, StringComparison.Ordinal);
            Assert.DoesNotContain("native-package-stage-one", requestBody, StringComparison.Ordinal);
            await output.WaitForIdleFrameAfterTextAsync("NATIVE_INPUT_PROVIDER_OK", transformedMark, timeout.Token);

            await process.StandardInput.WriteAsync("/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await errors.WaitAsync(timeout.Token));
            Assert.Equal(1, requestBody.Split("native-package-final", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        var records = (await File.ReadAllLinesAsync(logPath, timeout.Token))
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var inputs = records.Select(record => record.RootElement).Where(record =>
                record.GetProperty("type").GetString() == "input").ToArray();
            Assert.Equal(["first", "second", "first", "second"], inputs.Select(record =>
                record.GetProperty("handler").GetString()));
            Assert.Equal(["Interactive", "Interactive", "Interactive", "Interactive"], inputs.Select(record =>
                record.GetProperty("source").GetString()));
            Assert.Equal(["native-package-handled", "native-package-handled", "native-package-transform",
                "native-package-stage-one"], inputs.Select(record => record.GetProperty("text").GetString()));
            Assert.All(inputs, record => Assert.Null(record.GetProperty("behavior").GetString()));
        }
        finally
        {
            foreach (var record in records) record.Dispose();
        }
    }

    [Fact]
    public async Task ExternalPackageReplacesInteractiveBangCommandsWithoutRunningLocalShellThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;

        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var package = await PackAsync(Path.Combine(fixture.Root, "package"));
        var install = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", package);
        Assert.True(install.ExitCode == 0, install.Error + "\n" + install.Output);

        var logPath = Path.Combine(fixture.Root, "interactive-bash.log");
        var fallback = Path.Combine(fixture.Root, "interactive-bash-fallback");
        var excludedFallback = Path.Combine(fixture.Root, "interactive-bash-excluded-fallback");
        var normalCommand = "touch " + ShellQuote(fallback);
        var excludedCommand = "touch " + ShellQuote(excludedFallback);
        var start = new ProcessStartInfo("/usr/bin/script")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"stty rows 24 cols 80; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --no-session --no-tools");
        start.ArgumentList.Add("/dev/null");
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(fixture.Root, "sessions");
        start.Environment["PISHARP_NATIVE_INPUT_BASH_LOG"] = logPath;
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                     "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
            start.Environment.Remove(name);

        using var process = Process.Start(start) ?? throw new IOException("Could not start the interactive process.");
        var output = new PtyOutputCapture(process);
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await output.InputReady.WaitAsync(timeout.Token);
            var normalMark = output.Mark;
            await process.StandardInput.WriteAsync("!" + normalCommand + "\r");
            await process.StandardInput.FlushAsync();
            try { await output.WaitForTextAsync("external-package-bash-result", normalMark, timeout.Token); }
            catch (OperationCanceledException error)
            {
                var log = File.Exists(logPath) ? await File.ReadAllTextAsync(logPath) : "<no extension log>";
                var stderr = errors.IsCompleted ? await errors : "<stderr still open>";
                throw new InvalidOperationException($"TUI full user-bash result was not rendered.\n{log}\n{output.CapturedOutput}\n{stderr}", error);
            }

            var excludedMark = output.Mark;
            await process.StandardInput.WriteAsync("!!" + excludedCommand + "\r");
            await process.StandardInput.FlushAsync();
            await output.WaitForTextAsync("external-package-bash-result", excludedMark, timeout.Token);

            await process.StandardInput.WriteAsync("/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await errors.WaitAsync(timeout.Token));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        Assert.False(File.Exists(fallback));
        Assert.False(File.Exists(excludedFallback));
        var records = (await File.ReadAllLinesAsync(logPath, timeout.Token))
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var bashes = records.Where(record =>
                record.RootElement.GetProperty("type").GetString() == "user_bash").ToArray();
            Assert.Equal(2, bashes.Length);
            Assert.Equal(normalCommand, bashes[0].RootElement.GetProperty("command").GetString());
            Assert.False(bashes[0].RootElement.GetProperty("excludeFromContext").GetBoolean());
            Assert.Equal(excludedCommand, bashes[1].RootElement.GetProperty("command").GetString());
            Assert.True(bashes[1].RootElement.GetProperty("excludeFromContext").GetBoolean());
            Assert.All(bashes, bash => Assert.Equal(fixture.ProjectDirectory,
                bash.RootElement.GetProperty("cwd").GetString()));
            Assert.DoesNotContain(records, record =>
                record.RootElement.GetProperty("type").GetString() == "input");
        }
        finally
        {
            foreach (var record in records) record.Dispose();
        }
    }

    private static Process Start(ManagedNpmFixture fixture, string sessions, string logPath,
        bool providerConfigured = false, bool failConfigure = false)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in new[] { "--mode", "rpc", "--offline", "--session-dir", sessions })
            start.ArgumentList.Add(argument);
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = sessions;
        start.Environment["PISHARP_NATIVE_INPUT_BASH_LOG"] = logPath;
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                     "PISHARP_SETTINGS_PATH", "PISHARP_MODELS_PATH", "PISHARP_AUTH_PATH", "PISHARP_FIXTURE_KEY" })
            start.Environment.Remove(name);
        if (providerConfigured)
        {
            start.ArgumentList.Add("--provider");
            start.ArgumentList.Add("fixture");
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add("fixture-model");
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-key";
        }
        if (failConfigure) start.Environment["PISHARP_NATIVE_INPUT_BASH_FAIL_CONFIGURE"] = "true";
        else start.Environment.Remove("PISHARP_NATIVE_INPUT_BASH_FAIL_CONFIGURE");
        return Process.Start(start) ?? throw new IOException("Could not start the RPC acceptance process.");
    }

    private static async Task WriteAsync(Process process, object command, CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadResponseAsync(Process process, string id,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            using var record = JsonDocument.Parse(line);
            var root = record.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetString() != id ||
                !root.TryGetProperty("type", out var type) || type.GetString() != "response") continue;
            return JsonDocument.Parse(line);
        }
        throw new EndOfStreamException($"RPC process closed before response {id}.");
    }

    private static async Task StopAsync(Process process)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    private static async Task<string> PackAsync(string output)
    {
        Directory.CreateDirectory(output);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("PiSharp.slnx not found.");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "pack", "tests/fixtures/NativeInputBashExtension/NativeInputBashExtension.csproj",
                     "--no-restore", "--output", output, "--nologo", "--disable-build-servers", "-m:1",
                     "-p:NuGetAudit=false" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not pack the input-bash fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        Assert.True(process.ExitCode == 0, await stdout + await stderr);
        return Assert.Single(Directory.GetFiles(output, "*.nupkg"));
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
