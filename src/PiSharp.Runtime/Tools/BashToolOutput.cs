using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Tools;

internal sealed record BashToolOutput(
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("exit_code")] int ExitCode,
    [property: JsonPropertyName("wall_time_seconds")] double WallTimeSeconds,
    [property: JsonPropertyName("full_output_path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FullOutputPath)
{
    public const int MaximumBytes = 1024 * 1024;
    public static JsonElement Schema { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"output":{"type":"string"},"truncated":{"type":"boolean"},
        "exit_code":{"type":"integer"},"wall_time_seconds":{"type":"number"},"full_output_path":{"type":"string"}},
        "required":["output","truncated","exit_code","wall_time_seconds"],"additionalProperties":false}
        """);

    public static PiSharpToolResult Project(BashExecutionResult result)
    {
        var code = result.ExitCode ?? throw new InvalidOperationException("Command terminated without an exit code.");
        var output = new BashToolOutput(result.StructuredOutput ?? result.Output, result.StructuredTruncated,
            code, result.WallTimeSeconds, result.StructuredTruncated ? result.FullOutputPath : null);
        var error = code == 0 ? null : $"Command exited with code {code}";
        return new(error is null ? result.DisplayOutput : result.DisplayOutput + "\n\n" + error,
            new { truncated = result.Truncated, fullOutputPath = result.FullOutputPath },
            JsonSerializer.SerializeToElement(output), IsError: error is not null, Error: error);
    }
}
