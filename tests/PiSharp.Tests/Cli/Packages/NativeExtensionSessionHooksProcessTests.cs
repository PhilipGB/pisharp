using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class NativeExtensionSessionHooksProcessTests
{
    private sealed class StreamCapture
    {
        private readonly object _gate = new();
        private readonly System.Text.StringBuilder _contents = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StreamCapture(StreamReader reader) => Completion = ReadAsync(reader);

        public Task Completion { get; }

        private async Task ReadAsync(StreamReader source)
        {
            var buffer = new char[1024];
            int count;
            while ((count = await source.ReadAsync(buffer)) > 0)
            {
                lock (_gate)
                {
                    _contents.Append(buffer, 0, count);
                    var changed = _changed;
                    _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    changed.TrySetResult();
                }
            }
        }

        public string Contents { get { lock (_gate) return _contents.ToString(); } }

        public async Task WaitForAsync(string text, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (_contents.ToString().Contains(text, StringComparison.Ordinal)) return;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(cancellationToken);
            }
        }
    }

    [Fact]
    public async Task ExternalPackageSuppliesCanonicalCompactionWithoutCallingProviderAndSurvivesReload()
    {
        if (!OperatingSystem.IsLinux()) return;

        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var sessionDirectory = Path.Combine(fixture.Root, "sessions");
        Directory.CreateDirectory(sessionDirectory);
        var packageDirectory = Path.Combine(fixture.Root, "session-hooks-package");
        var packagePath = await PackExternalSessionHooksPackageAsync(packageDirectory);
        AssertPublicAbstractionsOnly(packagePath);

        const string modelId = "native-session-hooks-process-fixture";
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "models.json"),
            JsonSerializer.Serialize(new
            {
                providers = new
                {
                    fixtureProvider = new
                    {
                        baseUrl = "http://127.0.0.1:1/v1",
                        apiKeyEnv = "PISHARP_FIXTURE_KEY",
                        models = new[] { new { id = modelId, api = "openai-completions" } }
                    }
                }
            }));
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "settings.json"),
            "{\"compaction\":{\"keepRecentTokens\":1}}");

        var install = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", packagePath);
        Assert.Equal(0, install.ExitCode);
        Assert.Contains("Installed PiSharp-native extension", install.Output, StringComparison.Ordinal);

        var conversation = new ConversationSession(fixture.ProjectDirectory, modelId,
            "http://127.0.0.1:1/v1", "fixtureProvider");
        conversation.Append(new ChatMessage(ChatRole.User, "first question"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "first answer"));
        conversation.Append(new ChatMessage(ChatRole.User, "second question"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "second answer"));
        var forkEntryId = conversation.Tree.Entries[0].Id;
        var originalSessionId = conversation.Id;
        var store = new ConversationStore(fixture.ProjectDirectory, sessionDirectory);
        var sessionPath = store.NewPath(conversation);
        await store.SaveAsync(conversation, sessionPath);
        var destination = new ConversationSession(fixture.ProjectDirectory, modelId,
            "http://127.0.0.1:1/v1", "fixtureProvider");
        destination.Append(new ChatMessage(ChatRole.User, "destination session"));
        var destinationPath = store.NewPath(destination);
        await store.SaveAsync(destination, destinationPath);
        var allowedDestination = new ConversationSession(fixture.ProjectDirectory, modelId,
            "http://127.0.0.1:1/v1", "fixtureProvider");
        allowedDestination.Append(new ChatMessage(ChatRole.User, "allowed destination session"));
        var allowedDestinationPath = store.NewPath(allowedDestination);
        await store.SaveAsync(allowedDestination, allowedDestinationPath);

        var logPath = Path.Combine(fixture.Root, "session-hooks.log");
        var process = StartRpcProcess(fixture, sessionDirectory, sessionPath, logPath,
            cancelSwitch: destinationPath, cancelFork: forkEntryId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteLineAsync("{\"id\":\"custom-compact\",\"type\":\"compact\"}"
                .AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);

            JsonDocument? response = null;
            var outputLines = new List<string>();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                outputLines.Add(line);
                using var record = JsonDocument.Parse(line);
                if (!record.RootElement.TryGetProperty("id", out var id) || id.GetString() != "custom-compact" ||
                    !record.RootElement.TryGetProperty("type", out var type) || type.GetString() != "response")
                    continue;
                response = JsonDocument.Parse(line);
                break;
            }

            Assert.NotNull(response);
            var responseSuccess = response!.RootElement.GetProperty("success").GetBoolean();
            var responseText = response.RootElement.ToString();
            Assert.Equal("compact", response.RootElement.GetProperty("command").GetString());
            response.Dispose();

            using var newVeto = await SendRpcRequestAsync(process,
                new { id = "veto-switch", type = "switch_session", sessionPath = destinationPath },
                "veto-switch", timeout.Token);
            Assert.False(newVeto.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("cancelled by an extension", newVeto.RootElement.GetProperty("error").GetString(),
                StringComparison.OrdinalIgnoreCase);
            using var forkVeto = await SendRpcRequestAsync(process,
                new { id = "veto-fork", type = "fork", entryId = forkEntryId }, "veto-fork", timeout.Token);
            Assert.False(forkVeto.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("cancelled by an extension", forkVeto.RootElement.GetProperty("error").GetString(),
                StringComparison.OrdinalIgnoreCase);

            var allowedForkEntryId = conversation.Tree.Entries[2].Id;
            using var allowedFork = await SendRpcRequestAsync(process,
                new { id = "allowed-fork", type = "fork", entryId = allowedForkEntryId },
                "allowed-fork", timeout.Token);
            Assert.True(allowedFork.RootElement.GetProperty("success").GetBoolean());

            using var allowedResume = await SendRpcRequestAsync(process,
                new { id = "allowed-resume", type = "switch_session", sessionPath = allowedDestinationPath },
                "allowed-resume", timeout.Token);
            Assert.True(allowedResume.RootElement.GetProperty("success").GetBoolean());

            using var allowedNew = await SendRpcRequestAsync(process,
                new { id = "allowed-new", type = "new_session" }, "allowed-new", timeout.Token);
            Assert.True(allowedNew.RootElement.GetProperty("success").GetBoolean());

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var standardErrorText = await standardError.WaitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Connection refused", standardErrorText, StringComparison.OrdinalIgnoreCase);

            var events = File.Exists(logPath) ? await File.ReadAllLinesAsync(logPath, timeout.Token) : [];
            Assert.True(responseSuccess, responseText + "\n" + standardErrorText + "\n" +
                string.Join("\n", events) + "\nRPC records:\n" + string.Join("\n", outputLines));
            Assert.Contains(events, line => line.StartsWith("before-compact:Manual:False:", StringComparison.Ordinal));
            Assert.Contains(events, line => line.StartsWith("compact:True:Manual:", StringComparison.Ordinal) &&
                line.EndsWith(":known summary from external package", StringComparison.Ordinal));
            Assert.DoesNotContain(events, line => line.StartsWith("compact-failed:", StringComparison.Ordinal));
            Assert.Contains(events, line => line.StartsWith($"before-switch:Resume:{originalSessionId}:{destinationPath}", StringComparison.Ordinal));
            Assert.Contains(events, line => line == $"before-fork:{forkEntryId}:Before");
            Assert.Contains(events, line => line == $"before-fork:{allowedForkEntryId}:Before");
            Assert.Contains(events, line => line.StartsWith($"before-switch:Resume:", StringComparison.Ordinal) &&
                line.EndsWith($":{allowedDestinationPath}", StringComparison.Ordinal));
            Assert.Contains(events, line => line.StartsWith("before-switch:New:", StringComparison.Ordinal));
            Assert.Contains(events, line => line.StartsWith("session-shutdown:New:", StringComparison.Ordinal));
            Assert.Contains(events, line => line.StartsWith("session-start:New:", StringComparison.Ordinal));
            Assert.Single(events, line => line.StartsWith("session-shutdown:Resume:", StringComparison.Ordinal));
            // Opening the process session emits one Resume start before any requests. The vetoed
            // replacement must not produce another start or shut down that outgoing session.
            Assert.Single(events, line => line == $"session-start:Resume:{originalSessionId}");
            Assert.Single(events, line => line == $"session-start:Resume:{allowedDestination.Id}");
            Assert.Single(events, line => line.StartsWith("session-shutdown:Fork:", StringComparison.Ordinal));
            Assert.Single(events, line => line.StartsWith("session-start:Fork:", StringComparison.Ordinal));

            var reloaded = await store.LoadAsync(sessionPath, timeout.Token);
            var compaction = Assert.Single(reloaded.Tree.ActivePath(), entry => entry.Type == "compaction");
            Assert.Equal("known summary from external package", compaction.Payload.GetProperty("summary").GetString());
            Assert.True(compaction.Payload.GetProperty("fromHook").GetBoolean());
            Assert.Equal("external-session-hooks", compaction.Payload.GetProperty("details")
                .GetProperty("fixture").GetString());
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
        }
    }

    [Fact]
    public async Task ExternalPackageVetoesAndObservesTreeNavigationThroughTui()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;

        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var packageDirectory = Path.Combine(fixture.Root, "session-hooks-package");
        var packagePath = await PackExternalSessionHooksPackageAsync(packageDirectory);
        var store = new ConversationStore(fixture.ProjectDirectory, Path.Combine(fixture.Root, "sessions"));
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "models.json"), JsonSerializer.Serialize(new
        {
            providers = new
            {
                fixtureProvider = new
                {
                    baseUrl = "http://127.0.0.1:1/v1",
                    apiKeyEnv = "PISHARP_FIXTURE_KEY",
                    models = new[] { new { id = "native-session-hooks-tree-fixture", api = "openai-completions" } }
                }
            }
        }));
        var install = await NativeExtensionStateProcessTests.RunCliAsync(fixture, "install", packagePath);
        Assert.Equal(0, install.ExitCode);

        var conversation = new ConversationSession(fixture.ProjectDirectory,
            "native-session-hooks-tree-fixture", "http://127.0.0.1:1/v1", "fixtureProvider");
        conversation.Append(new ChatMessage(ChatRole.User, "common root"));
        var rootEntry = conversation.Tree.HeadId!;
        conversation.Append(new ChatMessage(ChatRole.Assistant, "common answer"));
        conversation.Append(new ChatMessage(ChatRole.User, "original branch"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "original answer"));
        var originalLeaf = conversation.Tree.HeadId!;
        conversation.Tree.Select(rootEntry);
        conversation.Append(new ChatMessage(ChatRole.User, "blocked branch"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "blocked answer"));
        var blockedLeaf = conversation.Tree.HeadId!;
        conversation.Tree.Select(rootEntry);
        conversation.Append(new ChatMessage(ChatRole.User, "allowed branch"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "allowed answer"));
        var allowedLeaf = conversation.Tree.HeadId!;
        conversation.Tree.Select(originalLeaf);
        var sessionPath = store.NewPath(conversation);
        await store.SaveAsync(conversation, sessionPath);

        var logPath = Path.Combine(fixture.Root, "tree-session-hooks.log");
        var process = StartTuiSessionHooksProcess(fixture, store.DirectoryPath, sessionPath, logPath, blockedLeaf);
        var stdout = new StreamCapture(process.StandardOutput);
        var stderr = new StreamCapture(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await stdout.WaitForAsync("\u001b[?2004h", timeout.Token);
            await process.StandardInput.WriteAsync(
                $"/branch {blockedLeaf[..12]}\r/branch {allowedLeaf[..12]}\r".AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            await stdout.WaitForAsync("Session tree navigation was cancelled by an extension.", timeout.Token);
            await stdout.WaitForAsync($"Selected {allowedLeaf[..12]}", timeout.Token);
            await process.StandardInput.WriteAsync("/quit\r".AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout.Completion, stderr.Completion).WaitAsync(timeout.Token);

            Assert.Equal(1, process.ExitCode);
            var events = await File.ReadAllLinesAsync(logPath, timeout.Token);
            var beforeTree = events.Where(line => line.StartsWith("before-tree:", StringComparison.Ordinal))
                .Select(line => line.Split(':')).ToArray();
            Assert.Equal(2, beforeTree.Length);
            Assert.Equal(blockedLeaf, beforeTree[0][2]);
            Assert.Equal(allowedLeaf, beforeTree[1][2]);
            Assert.Equal(beforeTree[0][1], beforeTree[1][1]);
            Assert.DoesNotContain(events, line => line.EndsWith($":{blockedLeaf}", StringComparison.Ordinal) &&
                line.StartsWith("tree:", StringComparison.Ordinal));
            Assert.Contains(events, line => line == $"tree:{beforeTree[0][1]}:{allowedLeaf}");
            var reloaded = await store.LoadAsync(sessionPath, timeout.Token);
            Assert.Equal(allowedLeaf, reloaded.Tree.HeadId);
            Assert.Contains(reloaded.Tree.Entries, entry => entry.Id == blockedLeaf);
            Assert.DoesNotContain("Agent error:", stdout.Contents, StringComparison.Ordinal);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
        }
    }

    private static Process StartRpcProcess(ManagedNpmFixture fixture, string sessionDirectory,
        string sessionPath, string logPath, string? cancelSwitch = null, string? cancelFork = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in new[] { "--mode", "rpc", "--provider", "fixtureProvider", "--model",
                     "native-session-hooks-process-fixture", "--offline", "--no-tools", "--session", sessionPath,
                     "--session-dir", sessionDirectory })
            start.ArgumentList.Add(argument);
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                     "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;
        start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
        start.Environment["NATIVE_SESSION_HOOKS_CUSTOM_SUMMARY"] = "known summary from external package";
        start.Environment["NATIVE_SESSION_HOOKS_LOG_PATH"] = logPath;
        if (cancelSwitch is not null) start.Environment["NATIVE_SESSION_HOOKS_CANCEL_SWITCH"] = cancelSwitch;
        if (cancelFork is not null) start.Environment["NATIVE_SESSION_HOOKS_CANCEL_FORK"] = cancelFork;
        return Process.Start(start) ?? throw new IOException("Could not start external session-hooks RPC process.");
    }

    private static Process StartTuiSessionHooksProcess(ManagedNpmFixture fixture, string sessionDirectory,
        string sessionPath, string logPath, string cancelTree)
    {
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
        start.ArgumentList.Add($"stty rows 30 cols 100; exec dotnet '{typeof(CliArguments).Assembly.Location}' --provider fixtureProvider --model native-session-hooks-tree-fixture --offline --no-tools --session '{sessionPath}' --session-dir '{sessionDirectory}'");
        start.ArgumentList.Add("/dev/null");
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                     "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_FIXTURE_KEY" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;
        start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-only-key";
        start.Environment["NATIVE_SESSION_HOOKS_CANCEL_TREE"] = cancelTree;
        start.Environment["NATIVE_SESSION_HOOKS_LOG_PATH"] = logPath;
        return Process.Start(start) ?? throw new IOException("Could not start external session-hooks TUI process.");
    }

    private static async Task<JsonDocument> SendRpcRequestAsync(Process process, object request, string id,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        var records = new List<string>();
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            records.Add(line);
            using var record = JsonDocument.Parse(line);
            if (record.RootElement.TryGetProperty("id", out var responseId) && responseId.GetString() == id &&
                record.RootElement.TryGetProperty("type", out var type) && type.GetString() == "response")
                return JsonDocument.Parse(line);
        }
        await process.WaitForExitAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        throw new EndOfStreamException($"RPC process closed before request {id} completed with exit code {process.ExitCode}. " +
            $"Records: {string.Join(" | ", records)}. Error: {error}");
    }

    private static async Task<string> PackExternalSessionHooksPackageAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PiSharp.slnx"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Could not locate PiSharp.slnx.");

        var project = Path.Combine(root.FullName, "tests", "fixtures", "NativeSessionHooksExtension",
            "NativeSessionHooksExtension.csproj");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "pack", project, "--configuration", BuildConfiguration(), "--no-restore",
                     "--output", outputDirectory, "--nologo", "--disable-build-servers", "-p:NuGetAudit=false" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start dotnet pack.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet pack failed:\n{await stderr}\n{await stdout}");

        var package = Path.Combine(outputDirectory, "PiSharp.TestExtensions.NativeSessionHooks.1.0.0.nupkg");
        Assert.True(File.Exists(package), $"dotnet pack did not create the package. Output: {await stdout}");
        return package;
    }

    private static void AssertPublicAbstractionsOnly(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntry = Assert.Single(archive.Entries, entry => entry.FullName.EndsWith(".nuspec",
            StringComparison.OrdinalIgnoreCase));
        using var nuspecStream = nuspecEntry.Open();
        var nuspec = XDocument.Load(nuspecStream).ToString(SaveOptions.DisableFormatting);
        Assert.Contains("PiSharp.Extensions.Abstractions", nuspec, StringComparison.Ordinal);
        Assert.DoesNotContain("PiSharp.Runtime", nuspec, StringComparison.Ordinal);
        Assert.DoesNotContain("PiSharp.Cli", nuspec, StringComparison.Ordinal);
        Assert.DoesNotContain("PiSharp.Runtime.dll", archive.Entries.Select(entry => entry.FullName));
        Assert.DoesNotContain("PiSharp.Cli.dll", archive.Entries.Select(entry => entry.FullName));
    }

    private static string BuildConfiguration() => new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
}
