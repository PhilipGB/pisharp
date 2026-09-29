using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Mcp;

/// <summary>Connects configured MCP servers and registers their tools through the extension policy surface.</summary>
public static class McpRuntime
{
    public static async Task<IReadOnlyList<string>> RegisterAsync(McpConfiguration configuration,
        ExtensionCatalog catalog, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>(configuration.Errors);
        var resourceServers = new List<(string Name, McpClient Client, TimeSpan Timeout, ToolExposure Exposure)>();
        foreach (var server in configuration.Servers.Where(server => server.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            McpClient? client = null;
            IClientTransport? transport = null;
            try
            {
                if (server.OAuth is not null)
                    throw new NotSupportedException("MCP OAuth is not configured yet.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(server.Timeout);
                if (server.Command is { } command)
                {
                    var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
                    foreach (var (key, value) in server.Environment) environment[key] = Expand(value);
                    transport = new StdioClientTransport(new StdioClientTransportOptions
                    {
                        Name = server.Name,
                        Command = Expand(command),
                        Arguments = server.Arguments.Select(Expand).ToArray(),
                        WorkingDirectory = server.WorkingDirectory is null ? workingDirectory :
                            Path.GetFullPath(Expand(server.WorkingDirectory), workingDirectory),
                        InheritEnvironmentVariables = false,
                        EnvironmentVariables = environment
                    });
                }
                else
                {
                    transport = new HttpClientTransport(new HttpClientTransportOptions
                    {
                        Name = server.Name,
                        Endpoint = server.Url!,
                        TransportMode = HttpTransportMode.StreamableHttp,
                        AdditionalHeaders = server.Headers.ToDictionary(item => item.Key,
                            item => Expand(item.Value), StringComparer.Ordinal),
                        ConnectionTimeout = server.Timeout
                    });
                }
                client = await McpClient.CreateAsync(transport, new McpClientOptions
                {
                    DiscoverProbeTimeout = TimeSpan.FromMilliseconds(750)
                }, cancellationToken: deadline.Token);
                var tools = client.ServerCapabilities.Tools is null
                    ? []
                    : await client.ListToolsAsync(cancellationToken: deadline.Token);
                var namedTools = new List<(McpClientTool Tool, string Name)>();
                var taken = catalog.Registration.ToolDefinitions.Select(item => item.Function.Name)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var tool in tools)
                {
                    var name = NameFor(server.Name, tool.Name, taken);
                    taken.Add(name);
                    namedTools.Add((tool, name));
                }
                foreach (var (tool, name) in namedTools)
                {
                    var exposure = server.ExposureFor(tool.Name);
                    var function = new McpToolFunction(tool, name, server.Timeout);
                    catalog.Registration.AddTool(new PiSharpToolRegistration(function, MapExposure(exposure),
                        DefaultActive: exposure == McpToolExposure.Direct,
                        Namespace: new PiSharpToolNamespace("mcp__" + server.Name,
                            "Tools from MCP server " + server.Name),
                        AllowNestedInvocation: exposure is McpToolExposure.Codemode or McpToolExposure.CodemodeDeferred,
                        OutputSchema: tool.ProtocolTool.OutputSchema));
                }
                if (client.ServerCapabilities.Resources is not null && server.Exposure != McpToolExposure.Hidden)
                    resourceServers.Add((server.Name, client, server.Timeout, MapExposure(server.Exposure)));
                catalog.OwnConnection(client);
                client = null;
                transport = null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add("MCP server " + server.Name + " timed out.");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Connection errors may contain server credentials or authorization headers.
                errors.Add("MCP server " + server.Name + " could not connect (" + error.GetType().Name + ").");
            }
            finally
            {
                if (client is not null) await client.DisposeAsync();
                else if (transport is IAsyncDisposable disposable) await disposable.DisposeAsync();
            }
        }
        if (resourceServers.Count > 0)
        {
            var reserved = new[] { "list_mcp_resources", "list_mcp_resource_templates", "read_mcp_resource" };
            if (catalog.Registration.ToolDefinitions.Any(tool => reserved.Contains(tool.Function.Name, StringComparer.Ordinal)))
                errors.Add("MCP resource tools could not register because a tool name is already in use.");
            else new McpResourceTools(resourceServers).Configure(catalog.Registration);
        }
        return errors;
    }

    private static string Sanitize(string name) => new(name.Select(character =>
        char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());

    private static string NameFor(string server, string tool, ISet<string> taken)
    {
        var name = Sanitize("mcp__" + server + "__" + tool);
        if (name.Length <= 64 && !taken.Contains(name)) return name;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(server + "\0" + tool)))[..8];
        var shortened = name[..Math.Min(name.Length, 55)] + "_" + hash;
        if (taken.Contains(shortened)) throw new InvalidDataException("MCP tool name collision: " + shortened);
        return shortened;
    }

    private static string Expand(string value) => Regex.Replace(value, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", match =>
        Environment.GetEnvironmentVariable(match.Groups[1].Value) ??
        throw new InvalidOperationException("Missing environment variable " + match.Groups[1].Value + "."));

    private static ToolExposure MapExposure(McpToolExposure value) => value switch
    {
        McpToolExposure.Direct => ToolExposure.Direct,
        McpToolExposure.Deferred or McpToolExposure.CodemodeDeferred => ToolExposure.Deferred,
        McpToolExposure.Codemode => ToolExposure.CodeMode,
        _ => ToolExposure.Hidden
    };
}

internal sealed class McpToolFunction(McpClientTool tool, string name, TimeSpan timeout) : DelegatingAIFunction(tool)
{
    public override string Name => name;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var values = arguments.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var result = await tool.CallAsync(values, cancellationToken: deadline.Token);
        var lines = new List<string>();
        var images = new List<PiSharpToolImage>();
        foreach (var content in result.Content)
            switch (content)
            {
                case TextContentBlock text:
                    lines.Add(text.Text);
                    break;
                case ImageContentBlock image:
                    images.Add(new PiSharpToolImage(image.MimeType, Convert.ToBase64String(image.DecodedData.Span)));
                    break;
                default:
                    lines.Add(JsonSerializer.Serialize(content));
                    break;
            }
        var output = string.Join("\n", lines);
        return new PiSharpToolResult(output, StructuredContent: result.StructuredContent,
            IsError: result.IsError == true, Error: result.IsError == true ? output : null, Images: images);
    }
}
