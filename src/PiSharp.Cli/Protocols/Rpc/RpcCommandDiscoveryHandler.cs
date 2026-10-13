using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcCommandDiscoveryHandler(
    JsonLineWriter output,
    Func<ResourceCatalog?> resources,
    Func<ExtensionRegistration?> extensions)
{
    public async Task<bool> TryHandleAsync(string command, JsonElement? id, CancellationToken cancellationToken)
    {
        if (command != "get_commands") return false;

        var commands = new List<RpcCommand>();
        if (extensions() is { } activeExtensions)
        {
            foreach (var (name, info) in activeExtensions.CommandInfo)
                commands.Add(new(name, info.Description, "extension", Project(info.SourceInfo)));
        }
        if (resources() is { } activeResources)
        {
            commands.AddRange(activeResources.Prompts.Select(item =>
                new RpcCommand(item.Name, item.Description, "prompt", Project(item.SourceInfo))));
            commands.AddRange(activeResources.Skills.Select(item =>
                new RpcCommand("skill:" + item.Name, item.Description, "skill", Project(item.SourceInfo))));
        }

        await output.EmitAsync(new
        {
            id,
            type = "response",
            command,
            success = true,
            data = new { commands }
        }, cancellationToken);
        return true;
    }

    private static RpcCommandSourceInfo Project(ResourceSourceInfo sourceInfo) =>
        new(sourceInfo.Path, sourceInfo.Source, sourceInfo.Scope, sourceInfo.Origin, sourceInfo.BaseDir);

    private sealed record RpcCommand(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("sourceInfo")] RpcCommandSourceInfo SourceInfo);

    private sealed record RpcCommandSourceInfo(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("origin")] string Origin,
        [property: JsonPropertyName("baseDir"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BaseDir);
}
