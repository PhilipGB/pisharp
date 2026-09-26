using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using Microsoft.Extensions.AI;

namespace PiSharp.Tests;

public sealed class RpcSessionProcessTests
{
    [Fact]
    public async Task SwitchSessionRebuildsTheRuntimeForTheRecordedProjectDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-cross-project-" + Guid.NewGuid().ToString("N"));
        var sourceCwd = Path.Combine(root, "source");
        var targetCwd = Path.Combine(root, "target");
        var agentDirectory = Path.Combine(root, "agent");
        var sourceSessionDirectory = Path.Combine(root, "source-sessions");
        var targetSessionDirectory = Path.Combine(root, "target-sessions");
        Directory.CreateDirectory(sourceCwd);
        Directory.CreateDirectory(targetCwd);
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(Path.Combine(sourceCwd, ".pi", "prompts"));
        Directory.CreateDirectory(Path.Combine(targetCwd, ".pi", "prompts"));
        await File.WriteAllTextAsync(Path.Combine(sourceCwd, ".pi", "prompts", "source-prompt.md"), "Source prompt");
        await File.WriteAllTextAsync(Path.Combine(targetCwd, ".pi", "prompts", "target-prompt.md"), "Target prompt");
        var trust = new ProjectTrust(agentDirectory);
        await trust.SetAsync(sourceCwd, true);
        await trust.SetAsync(targetCwd, true);
        var sourceStore = new ConversationStore(sourceCwd, sourceSessionDirectory);
        var targetStore = new ConversationStore(targetCwd, targetSessionDirectory);
        var source = new ConversationSession(sourceCwd, ConnectionSettings.LocalModel,
            new Uri(ConnectionSettings.LocalEndpoint).ToString(), "local");
        source.Append(new ChatMessage(ChatRole.User, "source project"));
        var sourcePath = sourceStore.NewPath(source);
        await sourceStore.SaveAsync(source, sourcePath);
        var target = new ConversationSession(targetCwd, ConnectionSettings.LocalModel,
            new Uri(ConnectionSettings.LocalEndpoint).ToString(), "local");
        target.Append(new ChatMessage(ChatRole.User, "target project"));
        var targetPath = targetStore.NewPath(target);
        await targetStore.SaveAsync(target, targetPath);
        var targetJsonlPath = Path.Combine(root, "target.jsonl");
        await PiJsonlSessionInterchange.ExportToFileAsync(target, targetJsonlPath);

        Process? process = null;
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = sourceCwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--local", "--offline", "--session", sourcePath })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_SESSION_DIR"] = sourceSessionDirectory;
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"source-commands\",\"type\":\"get_commands\"}");
            await process.StandardInput.WriteLineAsync($"{{\"id\":\"switch\",\"type\":\"switch_session\",\"sessionPath\":\"{targetPath}\"}}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"target-commands\",\"type\":\"get_commands\"}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"state\",\"type\":\"get_state\"}");
            await process.StandardInput.WriteLineAsync($"{{\"id\":\"new\",\"type\":\"new_session\",\"parentSession\":\"{targetPath}\"}}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"new-state\",\"type\":\"get_state\"}");
            await process.StandardInput.WriteLineAsync($"{{\"id\":\"switch-jsonl\",\"type\":\"switch_session\",\"sessionPath\":\"{targetJsonlPath}\"}}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"jsonl-entries\",\"type\":\"get_entries\"}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"jsonl-tree\",\"type\":\"get_tree\"}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"jsonl-state\",\"type\":\"get_state\"}");
            await process.StandardInput.WriteLineAsync($"{{\"id\":\"new-jsonl\",\"type\":\"new_session\",\"parentSession\":\"{targetJsonlPath}\"}}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"new-jsonl-state\",\"type\":\"get_state\"}");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error.WaitAsync(timeout.Token));
            var responses = (await output.WaitAsync(timeout.Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var sourceCommands = Assert.Single(responses,
                    response => response.RootElement.GetProperty("id").GetString() == "source-commands");
                var sourceNames = sourceCommands.RootElement.GetProperty("data").GetProperty("commands")
                    .EnumerateArray().Select(command => command.GetProperty("name").GetString()).ToArray();
                Assert.Contains("source-prompt", sourceNames);
                Assert.DoesNotContain("target-prompt", sourceNames);
                var switched = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "switch");
                Assert.True(switched.RootElement.GetProperty("success").GetBoolean());
                Assert.False(switched.RootElement.GetProperty("data").GetProperty("cancelled").GetBoolean());
                var targetCommands = Assert.Single(responses,
                    response => response.RootElement.GetProperty("id").GetString() == "target-commands");
                var targetNames = targetCommands.RootElement.GetProperty("data").GetProperty("commands")
                    .EnumerateArray().Select(command => command.GetProperty("name").GetString()).ToArray();
                Assert.Contains("target-prompt", targetNames);
                Assert.DoesNotContain("source-prompt", targetNames);
                var state = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "state");
                Assert.Equal(target.Id, state.RootElement.GetProperty("data").GetProperty("sessionId").GetString());
                var created = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "new");
                Assert.True(created.RootElement.GetProperty("success").GetBoolean());
                var newState = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "new-state");
                var newId = newState.RootElement.GetProperty("data").GetProperty("sessionId").GetString();
                var savedNew = Assert.Single(Directory.EnumerateFiles(targetSessionDirectory, "*.session.json")
                    .Select(path => (Path: path, Session: ConversationSession.Parse(File.ReadAllText(path)))),
                    item => item.Session.Id == newId);
                Assert.Equal(targetCwd, savedNew.Session.WorkingDirectory);
                Assert.Equal(targetPath, savedNew.Session.ParentSessionPath);
                var jsonlState = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "jsonl-state");
                Assert.Equal(target.Id, jsonlState.RootElement.GetProperty("data").GetProperty("sessionId").GetString());
                var jsonlCreated = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "new-jsonl");
                Assert.True(jsonlCreated.RootElement.GetProperty("success").GetBoolean());
                var jsonlNewState = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "new-jsonl-state");
                var jsonlNewId = jsonlNewState.RootElement.GetProperty("data").GetProperty("sessionId").GetString();
                var savedJsonlChild = Assert.Single(Directory.EnumerateFiles(root, "*.session.json")
                    .Select(path => (Path: path, Session: ConversationSession.Parse(File.ReadAllText(path)))),
                    item => item.Session.Id == jsonlNewId);
                Assert.Equal(targetCwd, savedJsonlChild.Session.WorkingDirectory);
                Assert.Equal(targetJsonlPath, savedJsonlChild.Session.ParentSessionPath);
                var jsonlEntriesResponse = Assert.Single(responses,
                    response => response.RootElement.GetProperty("id").GetString() == "jsonl-entries");
                var jsonlEntries = jsonlEntriesResponse.RootElement.GetProperty("data");
                Assert.Equal(["entries", "leafId"], jsonlEntries.EnumerateObject().Select(property => property.Name));
                Assert.False(jsonlEntries.TryGetProperty("format", out _));
                var jsonlEntryList = jsonlEntries.GetProperty("entries");
                var jsonlEntry = Assert.Single(jsonlEntryList.EnumerateArray(),
                    entry => entry.GetProperty("id").GetString() == target.Tree.Entries[0].Id);
                Assert.Equal("message", jsonlEntry.GetProperty("type").GetString());
                Assert.Equal(jsonlEntryList[jsonlEntryList.GetArrayLength() - 1].GetProperty("id").GetString(),
                    jsonlEntries.GetProperty("leafId").GetString());
                var jsonlTreeResponse = Assert.Single(responses,
                    response => response.RootElement.GetProperty("id").GetString() == "jsonl-tree");
                var jsonlTree = jsonlTreeResponse.RootElement.GetProperty("data");
                Assert.Equal(["tree", "leafId"], jsonlTree.EnumerateObject().Select(property => property.Name));
                var jsonlRoot = Assert.Single(jsonlTree.GetProperty("tree").EnumerateArray());
                Assert.Equal(["entry", "children"], jsonlRoot.EnumerateObject().Select(property => property.Name));
                Assert.Equal(target.Tree.Entries[0].Id, jsonlRoot.GetProperty("entry").GetProperty("id").GetString());
                Assert.Equal(jsonlEntries.GetProperty("leafId").GetString(), jsonlTree.GetProperty("leafId").GetString());
                var modelChange = Assert.Single(jsonlRoot.GetProperty("children").EnumerateArray());
                Assert.Equal("model_change", modelChange.GetProperty("entry").GetProperty("type").GetString());
            }
            finally { foreach (var response in responses) response.Dispose(); }
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
    public async Task ForkAndCloneReplaceTheSessionAndPersistParentLinks()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-session-branches-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(agentDirectory);
        var store = new ConversationStore(root, sessionDirectory);
        var source = new ConversationSession(root, ConnectionSettings.LocalModel,
            new Uri(ConnectionSettings.LocalEndpoint).ToString(), "local");
        source.Append(new ChatMessage(ChatRole.User, "fork this prompt"));
        var sourceEntryId = source.Tree.HeadId!;
        var sourcePath = store.NewPath(source);
        await store.SaveAsync(source, sourcePath);
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
            foreach (var argument in new[] { "--mode", "rpc", "--local", "--offline", "--session", sourcePath,
                "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"clone\",\"type\":\"clone\"}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"clone-state\",\"type\":\"get_state\"}");
            await process.StandardInput.WriteLineAsync($"{{\"id\":\"fork\",\"type\":\"fork\",\"entryId\":\"{sourceEntryId}\"}}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"fork-state\",\"type\":\"get_state\"}");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error.WaitAsync(timeout.Token));
            var responses = (await output.WaitAsync(timeout.Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var cloneResponse = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "clone");
                Assert.True(cloneResponse.RootElement.GetProperty("success").GetBoolean());
                Assert.False(cloneResponse.RootElement.GetProperty("data").GetProperty("cancelled").GetBoolean());
                var cloneState = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "clone-state");
                var forkResponse = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "fork");
                Assert.True(forkResponse.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal("fork this prompt", forkResponse.RootElement.GetProperty("data").GetProperty("text").GetString());
                Assert.False(forkResponse.RootElement.GetProperty("data").GetProperty("cancelled").GetBoolean());
                var forkState = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "fork-state");
                var cloneId = cloneState.RootElement.GetProperty("data").GetProperty("sessionId").GetString();
                var forkId = forkState.RootElement.GetProperty("data").GetProperty("sessionId").GetString();
                Assert.NotEqual(source.Id, cloneId);
                Assert.NotEqual(cloneId, forkId);

                var sessions = await SessionCatalog.ListAsync(store);
                Assert.Equal(3, sessions.Count);
                var cloneListing = Assert.Single(sessions, item => item.Id == cloneId);
                var forkListing = Assert.Single(sessions, item => item.Id == forkId);
                var cloneSession = await store.LoadAsync(cloneListing.Path);
                var forkSession = await store.LoadAsync(forkListing.Path);
                Assert.Equal(sourcePath, cloneSession.ParentSessionPath);
                Assert.Equal(cloneListing.Path, forkSession.ParentSessionPath);
                Assert.Empty(forkSession.ActiveMessages());
            }
            finally { foreach (var response in responses) response.Dispose(); }
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
    public async Task NewSessionSwitchesTheActiveRpcSessionAndTracksItsParent()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-new-session-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(agentDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
            foreach (var argument in new[] { "--mode", "rpc", "--local", "--offline", "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"new\",\"type\":\"new_session\",\"parentSession\":\"/sessions/parent.jsonl\"}");
            await process.StandardInput.WriteLineAsync("{\"id\":\"state\",\"type\":\"get_state\"}");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error.WaitAsync(timeout.Token));
            var responses = (await output.WaitAsync(timeout.Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var created = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "new");
                Assert.True(created.RootElement.GetProperty("success").GetBoolean());
                Assert.False(created.RootElement.GetProperty("data").GetProperty("cancelled").GetBoolean());
                var state = Assert.Single(responses, response => response.RootElement.GetProperty("id").GetString() == "state");
                Assert.True(state.RootElement.GetProperty("success").GetBoolean());
                var sessionId = state.RootElement.GetProperty("data").GetProperty("sessionId").GetString();
                Assert.Equal(2, Directory.GetFiles(sessionDirectory, "*.session.json").Length);
                var store = new ConversationStore(root, sessionDirectory);
                var createdSession = await store.LoadAsync(Assert.Single(Directory.GetFiles(sessionDirectory, "*.session.json",
                    SearchOption.TopDirectoryOnly), path =>
                    ConversationSession.Parse(File.ReadAllText(path)).Id == sessionId));
                Assert.Equal("/sessions/parent.jsonl", createdSession.ParentSessionPath);
            }
            finally { foreach (var response in responses) response.Dispose(); }
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
}
