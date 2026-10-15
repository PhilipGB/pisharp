using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Tests;

public sealed class ToolArgumentJsonTests
{
    [Theory]
    [InlineData("""{"path":"target.txt", BROKEN}""")]
    [InlineData("{\"path\":\"target.txt\"")]
    [InlineData("[]")]
    [InlineData("null")]
    public void CompletedArgumentsRejectPrefixesAndNonObjects(string source) =>
        Assert.ThrowsAny<JsonException>(() => ToolArgumentJson.ParseComplete(source));

    [Theory]
    [InlineData("", "{}")]
    [InlineData("""{"text":"\url{x}"}""", """{"text":"\\url{x}"}""")]
    public void CompletedArgumentsPreserveEmptyObjectsAndStringEscapeRepair(string source, string expected)
    {
        using var document = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(document.RootElement, ToolArgumentJson.ParseComplete(source)));
    }

    [Theory]
    [InlineData("""{"text":"\url{x}"}""", """{"text":"\\url{x}"}""")]
    [InlineData("""{"path":"a.tex","content":"\usepackage{x}","mode":"w"}""", """{"path":"a.tex","content":"\\usepackage{x}","mode":"w"}""")]
    [InlineData("""{"text":"caf\u00e9 \url"}""", """{"text":"café \\url"}""")]
    [InlineData("""{"text":"ab\u00""", """{"text":"ab"}""")]
    [InlineData("""{"text":"literal\\usepackage"}""", """{"text":"literal\\usepackage"}""")]
    [InlineData("""{"text":"\uD83D\uDE00"}""", """{"text":"😀"}""")]
    [InlineData("""{"a":1,"text":"partial""", """{"a":1,"text":"partial"}""")]
    [InlineData("""{"a":1,"b":""", """{"a":1}""")]
    [InlineData("""{"a":1,"n":1e""", """{"a":1,"n":1}""")]
    [InlineData("""{"a":1,"n":1.""", """{"a":1}""")]
    [InlineData("""{"a":tru""", """{"a":true}""")]
    [InlineData("""{"a":nul""", """{"a":null}""")]
    [InlineData("""{"a":[1,2,""", """{"a":[1,2]}""")]
    [InlineData("{\"text\":\"bad\\q\"", "{}")]
    [InlineData("""{"a":1,"text":"bad\q""", """{"a":1,"text":"bad"}""")]
    [InlineData("", "{}")]
    [InlineData("{\"text\":\"caf\\u00e9 ", "{\"text\":\"café\"}")]
    [InlineData("{\"text\":\"caf\\u00e9 \"}", "{\"text\":\"café \"}")]
    public void PreservesStringsAndCompletedFieldsDuringArgumentStreaming(string source, string expected)
    {
        using var document = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(document.RootElement, ToolArgumentJson.ParseStreaming(source)));
    }

    [Fact]
    public void EscapesRawControlsInsideStringsAndKeepsEscapedQuotes()
    {
        var source = "{\"text\":\"line\n\t\0\\\"quoted\\\"\",\"mode\":\"w\"}";
        var result = ToolArgumentJson.ParseStreaming(source);
        Assert.Equal("line\n\t\0\"quoted\"", result.GetProperty("text").GetString());
        Assert.Equal("w", result.GetProperty("mode").GetString());
    }
}
