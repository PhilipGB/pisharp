using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Mcp;

internal sealed class McpResourceTools(
    IReadOnlyList<(string Name, McpServerConnection Connection, TimeSpan Timeout, ToolExposure Exposure)> servers)
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public void Configure(ExtensionRegistration registration)
    {
        var exposure = servers.Any(server => server.Exposure == ToolExposure.Direct)
            ? ToolExposure.Direct
            : servers.Any(server => server.Exposure == ToolExposure.CodeMode)
                ? ToolExposure.CodeMode : ToolExposure.Deferred;
        var definitions = new PiSharpToolRegistration[]
        {
            new(AIFunctionFactory.Create(ListAsync,
                    name: "list_mcp_resources", description: "List resources from connected MCP servers."), exposure,
                DefaultActive: exposure == ToolExposure.Direct),
            new(AIFunctionFactory.Create(ListTemplatesAsync,
                    name: "list_mcp_resource_templates", description: "List resource templates from connected MCP servers."),
                exposure, DefaultActive: exposure == ToolExposure.Direct),
            new(AIFunctionFactory.Create(ReadAsync,
                    name: "read_mcp_resource", description: "Read a resource from a connected MCP server."), exposure,
                DefaultActive: exposure == ToolExposure.Direct)
        };
        var source = new ResourceSourceInfo("builtin:mcp", "builtin", "builtin", "top-level", null);
        registration.ReplaceOwnedTools("mcp:resources", definitions, source);
    }

    private async Task<PiSharpToolResult> ListAsync(CancellationToken cancellationToken,
        [Description("MCP server name; omit to list all connected servers.")] string? server = null)
    {
        var resources = new List<object>();
        var errors = new List<object>();
        foreach (var selected in Select(server))
            try
            {
                using var deadline = Deadline(selected.Timeout, cancellationToken);
                foreach (var item in await selected.Connection.ListResourcesAsync(deadline.Token))
                    if (!IsApp(item.Uri, item.MimeType))
                        resources.Add(new
                        {
                            server = selected.Name,
                            item.Uri,
                            item.Name,
                            item.Title,
                            item.Description,
                            item.MimeType
                        });
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add(new { server = selected.Name, error = error.GetType().Name });
            }
        var output = new { resources, errors };
        var json = JsonSerializer.SerializeToElement(output, s_json);
        return new PiSharpToolResult(json.ToString(), StructuredContent: json);
    }

    private async Task<PiSharpToolResult> ListTemplatesAsync(CancellationToken cancellationToken,
        [Description("MCP server name; omit to list all connected servers.")] string? server = null)
    {
        var templates = new List<object>();
        var errors = new List<object>();
        foreach (var selected in Select(server))
            try
            {
                using var deadline = Deadline(selected.Timeout, cancellationToken);
                foreach (var item in await selected.Connection.ListResourceTemplatesAsync(deadline.Token))
                    if (!IsApp(item.UriTemplate, item.MimeType))
                        templates.Add(new
                        {
                            server = selected.Name,
                            item.UriTemplate,
                            item.Name,
                            item.Title,
                            item.Description,
                            item.MimeType
                        });
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                errors.Add(new { server = selected.Name, error = error.GetType().Name });
            }
        var output = new { resourceTemplates = templates, errors };
        var json = JsonSerializer.SerializeToElement(output, s_json);
        return new PiSharpToolResult(json.ToString(), StructuredContent: json);
    }

    private async Task<PiSharpToolResult> ReadAsync(
        [Description("MCP server name returned by list_mcp_resources.")] string server,
        [Description("Resource URI returned by list_mcp_resources.")] string uri,
        CancellationToken cancellationToken)
    {
        var selected = Select(server).Single();
        if (IsApp(uri, null)) throw new ArgumentException("MCP App resources are not supported.", nameof(uri));
        using var deadline = Deadline(selected.Timeout, cancellationToken);
        var result = await selected.Connection.ReadResourceAsync(uri, deadline.Token);
        var text = new List<string>();
        var images = new List<PiSharpToolImage>();
        foreach (var content in result.Contents)
            switch (content)
            {
                case TextResourceContents item:
                    text.Add(item.Text);
                    break;
                case BlobResourceContents item when item.MimeType?.StartsWith("image/", StringComparison.Ordinal) == true:
                    images.Add(new PiSharpToolImage(item.MimeType, Convert.ToBase64String(item.DecodedData.Span)));
                    break;
                case BlobResourceContents item:
                    text.Add("Binary MCP resource: " + item.Uri + " (" + (item.MimeType ?? "unknown") + ")");
                    break;
            }
        var json = JsonSerializer.SerializeToElement(new { server, uri, contents = result.Contents }, s_json);
        return new PiSharpToolResult(string.Join("\n", text), StructuredContent: json, Images: images);
    }

    private IEnumerable<(string Name, McpServerConnection Connection, TimeSpan Timeout, ToolExposure Exposure)> Select(string? name)
    {
        if (name is null) return servers;
        var selected = servers.Where(server => server.Name == name).ToArray();
        if (selected.Length == 0) throw new ArgumentException("Unknown MCP resource server.", nameof(name));
        return selected;
    }

    private static CancellationTokenSource Deadline(TimeSpan timeout, CancellationToken parent)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(parent);
        source.CancelAfter(timeout);
        return source;
    }

    private static bool IsApp(string uri, string? mimeType) =>
        uri.StartsWith("ui://", StringComparison.OrdinalIgnoreCase) ||
        mimeType?.Contains("profile=mcp-app", StringComparison.OrdinalIgnoreCase) == true;
}
