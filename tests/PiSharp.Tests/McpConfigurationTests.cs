using PiSharp.Runtime.Mcp;

namespace PiSharp.Tests;

public sealed class McpConfigurationTests
{
    [Fact]
    public async Task TrustedProjectOverridesGlobalAndUntrustedProjectIsIgnored()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-config-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(project, ".pi"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "mcp.json"), """
                {"mcpServers":{
                  "shared":{"command":"global-server","args":["--one"],"exposure":"deferred"},
                  "disabled":{"url":"https://example.test/mcp","enabled":false}
                },"autoEnableCodemode":false}
                """);
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "mcp.json"), """
                {"mcpServers":{
                  "shared":{"command":"project-server","exposure":"hidden",
                    "toolExposure":{"list*":"codemode-deferred","list_secret":"direct"}},
                  "new":{"url":"https://project.test/mcp","headers":{"Authorization":"Bearer ${SECRET}"}}
                },"autoEnableCodemode":true}
                """);

            var untrusted = await McpConfiguration.LoadAsync(agent, project, false);
            Assert.False(untrusted.AutoEnableCodemode);
            Assert.Equal(2, untrusted.Servers.Count);
            Assert.Equal("global-server", untrusted.Servers.Single(server => server.Name == "shared").Command);
            Assert.False(untrusted.Servers.Single(server => server.Name == "disabled").Enabled);
            Assert.Empty(untrusted.Errors);

            var trusted = await McpConfiguration.LoadAsync(agent, project, true);
            Assert.True(trusted.AutoEnableCodemode);
            Assert.Equal(3, trusted.Servers.Count);
            var shared = trusted.Servers.Single(server => server.Name == "shared");
            Assert.Equal("project-server", shared.Command);
            Assert.Equal("project", shared.Scope);
            Assert.Equal(McpToolExposure.Hidden, shared.ExposureFor("other"));
            Assert.Equal(McpToolExposure.CodemodeDeferred, shared.ExposureFor("list_public"));
            Assert.Equal(McpToolExposure.Direct, shared.ExposureFor("list_secret"));
            Assert.Equal("Bearer ${SECRET}", trusted.Servers.Single(server => server.Name == "new")
                .Headers["Authorization"]);
            Assert.Empty(trusted.Errors);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InvalidEntriesAreReportedWithoutDroppingValidServersOrSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "mcp.json"), """
                {"mcpServers":{
                  "valid":{"url":"http://localhost:8123/mcp","exposure":"direct"},
                  "bad-name!":{"command":"secret-command"},
                  "invalid":{"url":"file:///secret-value","headers":{"Authorization":"very-secret"}},
                  "legacy":{"type":"sse","url":"http://localhost:8123/sse"}
                }}
                """);
            var loaded = await McpConfiguration.LoadAsync(root, root, false);
            Assert.Equal("valid", Assert.Single(loaded.Servers).Name);
            Assert.Equal(3, loaded.Errors.Count);
            Assert.DoesNotContain("very-secret", string.Join(' ', loaded.Errors));
            Assert.DoesNotContain("secret-value", string.Join(' ', loaded.Errors));
            Assert.DoesNotContain("secret-command", string.Join(' ', loaded.Errors));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
