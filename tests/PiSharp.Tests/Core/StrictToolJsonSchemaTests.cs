using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Tests;

public sealed class StrictToolJsonSchemaTests
{
    [Fact]
    public void RequiredPropertyOrderMatchesNumericJavaScriptKeysThenOtherNames()
    {
        using var source = JsonDocument.Parse("""
            {"type":"object","properties":{"2":{"type":"number"},"01":{"type":"string"},"1":{"type":"number"},"4294967295":{"type":"string"},"last":{"type":"string"}}}
            """);
        var strict = StrictToolJsonSchema.Create(source.RootElement);
        Assert.Equal(new[] { "1", "2", "01", "4294967295", "last" },
            strict.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void OptionalValuesBecomeNullableAndNestedObjectsCloseWithoutMutatingTheOriginal()
    {
        using var source = JsonDocument.Parse("""
            {"type":"object","properties":{"required":{"type":"string"},"optional":{"type":"number"},"alreadyNullable":{"type":["string","null"]},"list":{"type":"array","items":{"type":"object","properties":{"nested":{"type":"string"}}}}},"required":["required"]}
            """);
        var original = source.RootElement.GetRawText();
        var strict = StrictToolJsonSchema.Create(source.RootElement);
        Assert.False(strict.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(4, strict.GetProperty("required").GetArrayLength());
        var properties = strict.GetProperty("properties");
        Assert.Equal("null", properties.GetProperty("optional").GetProperty("anyOf")[1].GetProperty("type").GetString());
        Assert.False(properties.GetProperty("alreadyNullable").TryGetProperty("anyOf", out _));
        var item = properties.GetProperty("list").GetProperty("anyOf")[0].GetProperty("items");
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("nested", item.GetProperty("required")[0].GetString());
        Assert.Equal(original, source.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("true", "root schema must have type object")]
    [InlineData("{\"type\":\"array\"}", "root schema must have type object")]
    [InlineData("{\"type\":\"object\",\"$ref\":\"other\"}", "$ref schemas are unsupported")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":true}", "schema-valued or true additionalProperties is unsupported")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":null}", "schema-valued or true additionalProperties is unsupported")]
    [InlineData("{\"type\":\"object\",\"properties\":null}", "object properties must be a schema map")]
    [InlineData("{\"properties\":{}}", "properties require type object")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":true}}", "boolean schemas are unsupported")]
    [InlineData("{\"type\":\"object\",\"required\":[1]}", "object required must be a string array")]
    [InlineData("{\"type\":\"object\",\"required\":[\"unknown\"]}", "required contains an unknown property")]
    [InlineData("{\"type\":\"object\",\"anyOf\":[]}", "anyOf must contain at least one schema")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"anyOf\":[{\"type\":\"object\"},{\"type\":\"null\"}]}}}", "object and array unions are unsupported")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"array\",\"items\":[]}}}", "tuple schemas are unsupported")]
    public void UnsupportedSchemasRetainTheSpecificReason(string schema, string expected)
    {
        using var document = JsonDocument.Parse(schema);
        Assert.Equal(expected, Assert.Throws<ArgumentException>(() => StrictToolJsonSchema.Create(document.RootElement)).Message);
    }
}
