using System.Text.Json;
using System.Diagnostics;
using PiSharp.Cli;
using PiSharp.Runtime.Mcp;

namespace PiSharp.Tests;

public sealed class McpCommandTests
{
    [Fact]
    public async Task AddListRemoveAndProjectScopeWorkWithoutAChatModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-command-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(project);
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp_fixture.py");
            var output = new StringWriter();
            var errors = new StringWriter();
            Assert.Equal(0, await McpCommand.RunAsync(["add", "fixture", "--exposure", "direct", "--",
                "python3", fixture], agent, project, output, errors));
            Assert.Contains("Added global MCP server", output.ToString());
            Assert.Equal("", errors.ToString());
            using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(agent, "mcp.json"))))
                Assert.Equal("python3", saved.RootElement.GetProperty("mcpServers").GetProperty("fixture")
                    .GetProperty("command").GetString());

            output.GetStringBuilder().Clear();
            Assert.Equal(0, await McpCommand.RunAsync(["list", "--json"], agent, project, output, errors));
            using (var listed = JsonDocument.Parse(output.ToString()))
            {
                var server = Assert.Single(listed.RootElement.GetProperty("servers").EnumerateArray());
                Assert.Equal("connected", server.GetProperty("state").GetString());
                Assert.Contains(server.GetProperty("tools").EnumerateArray(), item =>
                    item.GetString() == "mcp__fixture__echo");
            }

            output.GetStringBuilder().Clear();
            Assert.Equal(0, await McpCommand.RunAsync(["add", "local", "--local", "--", "python3", fixture],
                agent, project, output, errors));
            Assert.Contains("not trusted", output.ToString());
            Assert.Equal(1, await McpCommand.RunAsync(["remove", "local"], agent, project, output, errors));
            Assert.Equal(0, await McpCommand.RunAsync(["remove", "local", "--local"], agent, project,
                output, errors));
            Assert.Equal(0, await McpCommand.RunAsync(["remove", "fixture"], agent, project, output, errors));
            using var removed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(agent, "mcp.json")));
            Assert.Empty(removed.RootElement.GetProperty("mcpServers").EnumerateObject());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InvalidAddsDoNotWriteAndHttpOptionsPreserveExistingConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = new StringWriter();
            var errors = new StringWriter();
            foreach (var invalid in new[]
            {
                new[] { "add", "bad name", "--", "cmd" },
                ["add", "x", "--url", "file:///secret"],
                ["add", "x", "--env", "BAD", "--", "cmd"],
                ["add", "x", "--url", "https://example.test/mcp", "--", "cmd"]
            })
                Assert.Equal(1, await McpCommand.RunAsync(invalid, root, root, output, errors));
            Assert.False(File.Exists(Path.Combine(root, "mcp.json")));
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"),
                """{"autoEnableCodemode":false,"mcpServers":{"old":{"command":"old"}}}""");
            Assert.Equal(0, await McpCommand.RunAsync(["add", "docs", "--url", "https://example.test/mcp",
                "--header", "X-Team=core", "--bearer-token-env-var", "DOCS_TOKEN", "--exposure", "deferred"],
                root, root, output, errors));
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "mcp.json")));
            Assert.False(saved.RootElement.GetProperty("autoEnableCodemode").GetBoolean());
            Assert.True(saved.RootElement.GetProperty("mcpServers").TryGetProperty("old", out _));
            var docs = saved.RootElement.GetProperty("mcpServers").GetProperty("docs");
            Assert.Equal("Bearer ${DOCS_TOKEN}", docs.GetProperty("headers").GetProperty("Authorization")
                .GetString());
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(root, "mcp.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentAddsRetainEveryServer()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-concurrent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "mcp.json");
            var server = JsonSerializer.SerializeToElement(new { command = "fixture" });
            await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
                McpConfigurationEditor.AddAsync(path, "s" + index, server)));
            var loaded = await McpConfiguration.LoadAsync(root, root, false);
            Assert.Empty(loaded.Errors);
            Assert.Equal(12, loaded.Servers.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task IndependentProcessesDoNotLoseConcurrentConfigUpdates()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-processes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var executions = Enumerable.Range(0, 6).Select(async index =>
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.Environment["PISHARP_AGENT_DIR"] = root;
                foreach (var argument in new[] { typeof(CliArguments).Assembly.Location, "mcp", "add", "p" + index,
                    "--", "python3", "fixture.py" }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                return (process.ExitCode, Output: await stdout, Error: await stderr);
            });
            var results = await Task.WhenAll(executions);
            Assert.All(results, result => Assert.True(result.ExitCode == 0, result.Error + result.Output));
            var loaded = await McpConfiguration.LoadAsync(root, root, false);
            Assert.Empty(loaded.Errors);
            Assert.Equal(6, loaded.Servers.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
