using System.Text.Json;
using System.Text;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class OpenAiToolSchemaCompatibilityTests
{
    [Fact]
    public void ReadSchemaOmitsOnlyTheOpenAiAdapterDefaultAdditionalPropertiesValue()
    {
        var request = """
            {
              "model": "fixture-model",
              "tools": [
                { "type": "function", "function": { "name": "read", "parameters": { "type": "object", "properties": {}, "additionalProperties": false } } },
                { "type": "function", "function": { "name": "extension_tool", "parameters": { "type": "object", "properties": {}, "additionalProperties": false } } }
              ]
            }
            """;

        var projected = OpenAiToolSchemaCompatibilityHandler.ProjectReadToolSchema(Encoding.UTF8.GetBytes(request));
        using var document = JsonDocument.Parse(projected);
        var tools = document.RootElement.GetProperty("tools");

        Assert.Equal("fixture-model", document.RootElement.GetProperty("model").GetString());
        Assert.False(tools[0].GetProperty("function").GetProperty("parameters").TryGetProperty("additionalProperties", out _));
        Assert.False(tools[1].GetProperty("function").GetProperty("parameters").GetProperty("additionalProperties").GetBoolean());
    }
}
