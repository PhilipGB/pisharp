using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class OpenAiToolSchemaCompatibilityTests
{
    [Theory]
    [InlineData("openai-completions")]
    [InlineData("openai-responses")]
    public void PhysicalBuiltinDeclarationsMatchPinnedWithoutAdapterObjectDefaults(string api)
    {
        using var fixture = SearchProcessProtocolTests.SourceFixture("provider", "before");
        var row = fixture.RootElement.GetProperty("cases").EnumerateArray().Single(row => row.GetProperty("api").GetString() == api);
        var expected = row.GetProperty("observations").GetProperty("pi").GetProperty("declarations");
        var native = row.GetProperty("observations").GetProperty("pisharp").GetProperty("declarations");
        var tools = new JsonArray();
        foreach (var declaration in native.EnumerateArray())
        {
            var function = JsonNode.Parse(declaration.GetRawText())!.AsObject();
            if (api == "openai-responses") function["strict"] = null;
            tools.Add(api == "openai-completions"
                ? new JsonObject { ["type"] = "function", ["function"] = function }
                : WithType(function));
        }
        var body = new JsonObject { ["tools"] = tools };
        using var projected = JsonDocument.Parse(OpenAiToolSchemaCompatibilityHandler.ProjectToolSchemas(Encoding.UTF8.GetBytes(body.ToJsonString())));
        var actual = projected.RootElement.GetProperty("tools");
        for (var index = 0; index < actual.GetArrayLength(); index++)
        {
            var function = api == "openai-completions" ? actual[index].GetProperty("function") : actual[index];
            Assert.Equal(expected[index].GetProperty("name").GetString(), function.GetProperty("name").GetString());
            Assert.False(function.TryGetProperty("strict", out _));
            Assert.True(JsonElement.DeepEquals(expected[index].GetProperty("parameters"), function.GetProperty("parameters")),
                api + " " + function.GetProperty("name").GetString());
        }
    }

    private static JsonObject WithType(JsonObject function)
    {
        function["type"] = "function";
        return function;
    }

    [Fact]
    public void ToolSchemasOmitAdapterEmptyRequiredAndBuiltinAdditionalPropertiesDefaults()
    {
        var request = """
            {
              "model": "fixture-model",
              "tools": [
                { "type": "function", "function": { "name": "read", "parameters": { "type": "object", "properties": {}, "additionalProperties": false } } },
                { "type": "function", "function": { "name": "bash", "parameters": { "type": "object", "properties": {}, "additionalProperties": false } } },
                { "type": "function", "function": { "name": "write", "parameters": { "type": "object", "properties": {}, "additionalProperties": false } } },
                { "type": "function", "function": { "name": "edit", "parameters": { "type": "object", "properties": {}, "additionalProperties": false } } },
                { "type": "function", "function": { "name": "extension_tool", "parameters": { "type": "object", "properties": {}, "required": [], "additionalProperties": false } } }
              ]
            }
            """;

        var projected = OpenAiToolSchemaCompatibilityHandler.ProjectToolSchemas(Encoding.UTF8.GetBytes(request));
        using var document = JsonDocument.Parse(projected);
        var tools = document.RootElement.GetProperty("tools");

        Assert.Equal("fixture-model", document.RootElement.GetProperty("model").GetString());
        Assert.False(tools[0].GetProperty("function").GetProperty("parameters").TryGetProperty("additionalProperties", out _));
        Assert.False(tools[1].GetProperty("function").GetProperty("parameters").TryGetProperty("additionalProperties", out _));
        Assert.False(tools[2].GetProperty("function").GetProperty("parameters").TryGetProperty("additionalProperties", out _));
        Assert.False(tools[3].GetProperty("function").GetProperty("parameters").TryGetProperty("additionalProperties", out _));
        Assert.False(tools[4].GetProperty("function").GetProperty("parameters").GetProperty("additionalProperties").GetBoolean());
        Assert.False(tools[4].GetProperty("function").GetProperty("parameters").TryGetProperty("required", out _));
    }
}
