using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PiSharp.Cli.Configuration;

/// <summary>Projects the current effective settings into the detached JSON form used by extensions.</summary>
internal static class NativeExtensionSettingsSnapshot
{
    private static readonly JsonSerializerOptions s_options = CreateOptions();

    public static JsonElement Create(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.SerializeToNode(settings, s_options)?.AsObject() ?? new JsonObject();
        // These convenience accessors are computed by the host and are not settings keys.
        json.Remove("terminalTrueColorOverride");
        json.Remove("skillCommandsEnabled");
        json.Remove("shareDestination");
        return JsonSerializer.SerializeToElement(json, s_options);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
