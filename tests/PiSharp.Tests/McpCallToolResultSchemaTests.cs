using System.Text.Json;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class McpCallToolResultSchemaTests
{
    [Fact]
    public void WrapsStructuredSchemaAndRelocatesOnlySchemaReferences()
    {
        using var source = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "hits": { "type": "array", "items": { "$ref": "#/$defs/Hit~1v1" } },
                "again": { "$ref": "#/properties/hits" },
                "children": { "type": "array", "items": { "$ref": "#" } },
                "default": { "$ref": "#/definitions/Hit" },
                "remote": { "$ref": "https://example.com/schema.json" }
              },
              "required": ["hits"],
              "$defs": {
                "Hit/v1": {
                  "type": "object",
                  "properties": { "id": { "type": "string" } },
                  "required": ["id"],
                  "default": { "$ref": "#/stays-data" }
                }
              },
              "definitions": {
                "Hit": { "type": "object", "properties": { "name": { "type": "string" } } }
              }
            }
            """);

        var schema = McpCallToolResultSchema.Create(source.RootElement.Clone());
        var resultSchema = schema.GetProperty("properties").GetProperty("structuredContent");
        Assert.Equal("#/properties/structuredContent/$defs/Hit~1v1",
            resultSchema.GetProperty("properties").GetProperty("hits").GetProperty("items")
                .GetProperty("$ref").GetString());
        Assert.Equal("#/properties/structuredContent/properties/hits",
            resultSchema.GetProperty("properties").GetProperty("again").GetProperty("$ref").GetString());
        Assert.Equal("#/properties/structuredContent",
            resultSchema.GetProperty("properties").GetProperty("children").GetProperty("items")
                .GetProperty("$ref").GetString());
        Assert.Equal("#/properties/structuredContent/definitions/Hit",
            resultSchema.GetProperty("properties").GetProperty("default").GetProperty("$ref").GetString());
        Assert.Equal("https://example.com/schema.json",
            resultSchema.GetProperty("properties").GetProperty("remote").GetProperty("$ref").GetString());
        Assert.Equal("#/stays-data", resultSchema.GetProperty("$defs").GetProperty("Hit/v1")
            .GetProperty("default").GetProperty("$ref").GetString());

        var validResult = JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text = "found" } },
            structuredContent = new
            {
                hits = new[] { new { id = "one" } },
                again = new[] { new { id = "one" } }
            },
            isError = false
        });
        new ToolResultSchemaValidator(schema).Validate(new PiSharpToolResult("found", StructuredContent: validResult));
    }
}
