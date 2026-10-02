using System.Text.Json;
using PiSharp.Cli.Authentication;

namespace PiSharp.Tests;

public sealed class RadiusMcpSetupTests
{
    [Theory]
    [InlineData("{}", "radius")]
    [InlineData("{\"mcpServers\":{\"radius\":{\"command\":\"unrelated\"}}}", "radius-mcp")]
    [InlineData("{\"mcpServers\":{\"custom\":{\"url\":\"https://radius.pi.dev/mcp/\",\"oauth\":{},\"headers\":{\"X-Extra\":\"kept\"},\"enabled\":false}}}", "custom")]
    public async Task AcceptedOfferUsesGlobalProviderAuthentication(string initial, string expectedName)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-radius-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "mcp.json");
            await File.WriteAllTextAsync(path, initial);
            var proposal = await RadiusMcpSetup.CreateAsync(root, root);
            Assert.NotNull(proposal);
            Assert.Equal(path, proposal.Path);
            Assert.Equal(expectedName, proposal.ServerName);
            Assert.Equal(initial, await File.ReadAllTextAsync(path));
            await proposal.SaveAsync();
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var server = saved.RootElement.GetProperty("mcpServers").GetProperty(expectedName);
            Assert.Equal("radius", server.GetProperty("auth").GetProperty("provider").GetString());
            Assert.False(server.TryGetProperty("oauth", out _));
            if (expectedName == "custom")
            {
                Assert.Equal("kept", server.GetProperty("headers").GetProperty("X-Extra").GetString());
                Assert.False(server.GetProperty("enabled").GetBoolean());
            }
            if (expectedName == "radius-mcp")
                Assert.Equal("unrelated", saved.RootElement.GetProperty("mcpServers").GetProperty("radius").GetProperty("command").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AlreadyConfiguredGlobalServerDoesNotOfferSetup()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-radius-configured-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "mcp.json");
            var initial = "{\"mcpServers\":{\"custom\":{\"url\":\"https://radius.pi.dev/mcp///\",\"auth\":{\"provider\":\"radius\"}}}}";
            await File.WriteAllTextAsync(path, initial);
            Assert.Null(await RadiusMcpSetup.CreateAsync(root, root));
            Assert.Equal(initial, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ProjectServerDoesNotSuppressGlobalSetupOffer()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-radius-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        try
        {
            var path = Path.Combine(root, ".pi", "mcp.json");
            var initial = "{\"mcpServers\":{\"custom\":{\"url\":\"https://radius.pi.dev/mcp\",\"auth\":{\"provider\":\"radius\"}}}}";
            await File.WriteAllTextAsync(path, initial);
            var proposal = await RadiusMcpSetup.CreateAsync(root, root);
            Assert.NotNull(proposal);
            Assert.Equal("radius", proposal.ServerName);
            Assert.Equal(Path.Combine(root, "mcp.json"), proposal.Path);
            Assert.Equal(initial, await File.ReadAllTextAsync(path));
            Assert.False(File.Exists(proposal.Path));
        }
        finally { Directory.Delete(root, true); }
    }
}
