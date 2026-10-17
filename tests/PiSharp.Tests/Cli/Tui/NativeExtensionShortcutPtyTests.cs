using System.Diagnostics;
using System.Text;
using PiSharp.Cli;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class NativeExtensionShortcutPtyTests
{
    private sealed class PtyOutputCapture
    {
        private readonly object _gate = new();
        private readonly StringBuilder _output = new();
        private readonly List<(string Text, int StartIndex, TaskCompletionSource Completion)> _waiters = [];
        private readonly TaskCompletionSource _inputReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PtyOutputCapture(Process process) => Completion = ReadAsync(process);

        public Task InputReady => _inputReady.Task;
        public Task<string> Completion { get; }
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
    public async Task ExternalPackageShortcutRunsAcrossReloadAndLeavesTuiEditorUsable()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;

        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var v1Package = await PackExternalFixtureAsync(fixture.Root, "v1", "1.0.0");
        var v2Package = await PackExternalFixtureAsync(fixture.Root, "v2", "2.0.0");
        var lifecyclePath = Path.Combine(fixture.Root, "lifecycle.log");
        var installed = await RunCliAsync(fixture, "install", v1Package);
        Assert.True(installed.ExitCode == 0, installed.Error + "\n" + installed.Output);

        Process? process = null;
        try
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
            start.ArgumentList.Add($"stty rows 24 cols 100; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --no-session --no-tools");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
            start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(fixture.Root, "sessions");
            start.Environment["PI_OFFLINE"] = "true";
            start.Environment["NATIVE_FIXTURE_LIFECYCLE_PATH"] = lifecyclePath;
            process = Process.Start(start);
            Assert.NotNull(process);

            var output = new PtyOutputCapture(process);
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await output.InputReady.WaitAsync(timeout.Token);
            var draftStart = output.CapturedOutput.Length;
            await process.StandardInput.WriteAsync("unfinished work");
            await process.StandardInput.FlushAsync();
            await output.WaitForTextAsync("unfinished work", draftStart, timeout.Token);
            await process.StandardInput.WriteAsync("\u001b[103;6u"); // Kitty key code 103 (g), Ctrl+Shift modifiers.
            await process.StandardInput.FlushAsync();
            await output.WaitForTextAsync("native-fixture-shortcut-v1", draftStart, timeout.Token);
            var v1EditorStart = output.CapturedOutput.Length;
            await process.StandardInput.WriteAsync(" continued");
            await process.StandardInput.FlushAsync();
            await output.WaitForTextAsync("unfinished work continued", v1EditorStart, timeout.Token);
            await process.StandardInput.WriteAsync("\u0015"); // Ctrl+U clears the draft without starting a provider turn.
            await process.StandardInput.FlushAsync();

            var updated = await RunCliAsync(fixture, "update", "--extension", v2Package);
            Assert.True(updated.ExitCode == 0, updated.Error + "\n" + updated.Output);
            var reloadOutputStart = output.CapturedOutput.Length;
            await process.StandardInput.WriteAsync("/reload\r");
            await process.StandardInput.FlushAsync();
            await output.WaitForTextAsync(
                "Reloaded keybindings, extensions, skills, prompts, themes, and context files",
                reloadOutputStart, timeout.Token);
            var lifecycleEvents = await File.ReadAllLinesAsync(lifecyclePath, timeout.Token);
            Assert.Contains("native-fixture-session-shutdown-v1:Reload", lifecycleEvents);
            Assert.Contains("native-fixture-session-start-v2:Reload:", lifecycleEvents);
            Assert.True(Array.IndexOf(lifecycleEvents, "native-fixture-session-shutdown-v1:Reload") <
                Array.IndexOf(lifecycleEvents, "native-fixture-session-start-v2:Reload:"),
                string.Join(Environment.NewLine, lifecycleEvents));

            var v2OutputStart = output.CapturedOutput.Length;
            await process.StandardInput.WriteAsync("\u001b[103;6u");
            await process.StandardInput.FlushAsync();
            await output.WaitForTextAsync("native-fixture-shortcut-v2", v2OutputStart, timeout.Token);

            var v2EditorStart = output.CapturedOutput.Length;
            await process.StandardInput.WriteAsync("editor-v2");
            await process.StandardInput.FlushAsync();
            await output.WaitForTextAsync("editor-v2", v2EditorStart, timeout.Token);
            await process.StandardInput.WriteAsync("\u0015");
            await process.StandardInput.FlushAsync();

            var removed = await RunCliAsync(fixture, "remove", "PiSharp.TestExtensions.NativeFixture");
            Assert.True(removed.ExitCode == 0, removed.Error + "\n" + removed.Output);
            var removalReloadStart = output.CapturedOutput.Length;
            await process.StandardInput.WriteAsync("/reload\r");
            await process.StandardInput.FlushAsync();
            await output.WaitForTextAsync(
                "Reloaded keybindings, extensions, skills, prompts, themes, and context files",
                removalReloadStart, timeout.Token);
            var removalEvents = await File.ReadAllLinesAsync(lifecyclePath, timeout.Token);
            Assert.Contains("native-fixture-session-shutdown-v2:Reload", removalEvents);

            var afterRemovalStart = output.CapturedOutput.Length;
            await process.StandardInput.WriteAsync("\u001b[103;6u");
            await process.StandardInput.FlushAsync();
            await process.StandardInput.WriteAsync("editor-after-removal");
            await process.StandardInput.FlushAsync();
            await output.WaitForTextAsync("editor-after-removal", afterRemovalStart, timeout.Token);
            Assert.DoesNotContain("native-fixture-shortcut-v2",
                output.CapturedOutput[afterRemovalStart..], StringComparison.Ordinal);
            await process.StandardInput.WriteAsync("\u0015");
            await process.StandardInput.FlushAsync();

            await process.StandardInput.WriteAsync("/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var capturedOutput = await output.Completion;

            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error:", capturedOutput);
            Assert.DoesNotContain("Exception:", await stderr);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
        }
    }

    private static async Task<string> PackExternalFixtureAsync(string root, string revision, string version)
    {
        var outputDirectory = Path.Combine(root, "packed-" + revision);
        Directory.CreateDirectory(outputDirectory);
        var repositoryRoot = FindRepositoryRoot();
        var fixtureProject = revision == "v2" ? "NativeExtensionFixtureV2" : "NativeExtensionFixture";
        var projectPath = Path.Combine(repositoryRoot, "tests", "fixtures", fixtureProject,
            fixtureProject + ".csproj");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "pack", projectPath, "--configuration", BuildConfiguration(), "--no-restore", "--output", outputDirectory,
            "--nologo", "--disable-build-servers", "-p:NuGetAudit=false", $"-p:NativeFixtureRevision={revision}",
            $"-p:NativeExtensionVersion={version}",
            $"-p:NativeFixtureDependencyVersion={(revision == "v2" ? "2.0.0" : "1.0.0")}"
        }) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new IOException("Could not start dotnet pack.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync();
            throw;
        }

        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet pack failed with exit code {process.ExitCode}:\n{stderr}\n{stdout}");
        var packagePath = Path.Combine(outputDirectory, $"PiSharp.TestExtensions.NativeFixture.{version}.nupkg");
        if (!File.Exists(packagePath)) throw new FileNotFoundException("dotnet pack did not create the fixture package.", packagePath);
        return packagePath;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(ManagedNpmFixture fixture,
        params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(fixture.Root, "sessions");
        start.Environment["npm_config_cache"] = Path.Combine(fixture.Root, "npm-cache");
        start.Environment["npm_config_offline"] = "true";
        start.Environment["PI_OFFLINE"] = "true";
        start.Environment.Remove("PISHARP_SETTINGS_PATH");
        using var process = Process.Start(start) ?? throw new IOException("Could not start PiSharp CLI.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    private static string BuildConfiguration() => new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find PiSharp.slnx.");
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
