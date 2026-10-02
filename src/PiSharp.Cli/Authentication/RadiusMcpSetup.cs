using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Runtime.Mcp;

namespace PiSharp.Cli.Authentication;

internal sealed record RadiusMcpSetup(string Path, string ServerName, JsonElement Configuration)
{
    internal const string ServerUrl = "https://radius.pi.dev/mcp";

    public static async Task<RadiusMcpSetup?> CreateAsync(string agentDirectory, string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var path = System.IO.Path.Combine(agentDirectory, "mcp.json");
        var loaded = await McpConfiguration.LoadAsync(agentDirectory, workingDirectory, projectTrusted: false,
            cancellationToken);
        var existing = loaded.Servers.FirstOrDefault(server =>
            server.Url?.OriginalString.TrimEnd('/') == ServerUrl);
        if (existing?.AuthProvider == "radius") return null;

        var name = existing?.Name ?? (loaded.Servers.Any(server => server.Name == "radius") ? "radius-mcp" : "radius");
        var config = existing is null
            ? new JsonObject { ["url"] = ServerUrl }
            : (JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!["mcpServers"]![existing.Name]!
                .DeepClone() as JsonObject)!;
        config["auth"] = new JsonObject { ["provider"] = "radius" };
        config.Remove("oauth");
        return new(path, name, JsonSerializer.SerializeToElement(config));
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        McpConfigurationEditor.AddAsync(Path, ServerName, Configuration, cancellationToken);
}
