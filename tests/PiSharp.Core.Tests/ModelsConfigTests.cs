using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Core.Models;

namespace PiSharp.Core.Tests;

/// <summary>models.json load, validation, and parsing behavior.</summary>
public class ModelsConfigTests
{
    [Fact]
    public async Task Load_MissingFile_EmptyConfig()
    {
        using var temp = TempDirectory.Create();
        var config = await ModelConfig.LoadAsync(Path.Combine(temp.Path, "models.json"));
        Assert.Empty(config.GetProviderIds());
        Assert.Null(config.GetError());
    }

    [Fact]
    public async Task Load_MalformedJson_ParseError()
    {
        // Pinned Pi (model-config.ts): BOM strip -> comment strip -> JSON.parse ->
        // schema validation. Malformed JSON is a parse error, not a schema error.
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "models.json");
        await File.WriteAllTextAsync(path, "{ not json");
        var config = await ModelConfig.LoadAsync(path);
        var error = config.GetError();
        Assert.NotNull(error);
        Assert.Contains("Failed to parse models.json", error);
        Assert.DoesNotContain("Invalid models.json schema", error);
    }

    [Fact]
    public async Task Load_ValidationFailure_ListsDiagnostics()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "models.json");
        await File.WriteAllTextAsync(path, """
        {
          "providers": {
            "bad": { "models": [ { "name": "no id" } ] }
          }
        }
        """);
        var config = await ModelConfig.LoadAsync(path);
        var error = config.GetError();
        Assert.NotNull(error);
        Assert.Contains("providers.bad.models.0.id", error);
        Assert.Contains("must have required property 'id'", error);
    }

    [Fact]
    public async Task Load_UnknownApiIdentifier_Parses()
    {
        // Pinned Pi (model-config.ts): api is Type.String({ minLength: 1 }), an
        // open string, not a closed enum. Provider/API compatibility is resolved
        // later ("No API provider registered for api: ..." at stream time). So an
        // unknown non-empty identifier must parse.
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "models.json");
        await File.WriteAllTextAsync(path, """
        {
          "providers": {
            "p": { "baseUrl": "http://p:1", "models": [ { "id": "m", "api": "bogus-api" } ] }
          }
        }
        """);
        var config = await ModelConfig.LoadAsync(path);
        Assert.Null(config.GetError());
        var model = config.GetProvider("p")!.Models![0];
        Assert.Equal("bogus-api", model.Api);
    }

    [Fact]
    public async Task Load_EmptyApi_SchemaError()
    {
        // A structurally invalid api value (empty string, violating minLength 1)
        // is a schema error.
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "models.json");
        await File.WriteAllTextAsync(path, """
        {
          "providers": {
            "p": { "baseUrl": "http://p:1", "models": [ { "id": "m", "api": "" } ] }
          }
        }
        """);
        var config = await ModelConfig.LoadAsync(path);
        var error = config.GetError();
        Assert.NotNull(error);
        Assert.Contains("Invalid models.json schema", error);
        Assert.Contains("providers.p.models.0.api", error);
    }

    [Fact]
    public async Task Load_ValidConfig_ParsesProviders()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "models.json");
        await File.WriteAllTextAsync(path, """
        {
          "providers": {
            "custom": {
              "name": "Custom",
              "baseUrl": "http://localhost:9999/v1",
              "apiKey": "sk-test",
              "models": [
                {
                  "id": "custom-1",
                  "name": "Custom 1",
                  "api": "openai-completions",
                  "reasoning": true,
                  "contextWindow": 4096,
                  "maxTokens": 1024,
                  "cost": { "input": 0.5, "output": 1.5, "cacheRead": 0.05, "cacheWrite": 0.625 },
                  "thinkingLevelMap": { "high": "high" }
                }
              ]
            }
          }
        }
        """);
        var config = await ModelConfig.LoadAsync(path);
        Assert.Null(config.GetError());
        var provider = config.GetProvider("custom");
        Assert.NotNull(provider);
        Assert.Equal("http://localhost:9999/v1", provider!.BaseUrl);
        var model = Assert.Single(provider.Models!);
        Assert.Equal("custom-1", model.Id);
        Assert.True(model.Reasoning);
        Assert.Equal(4096, model.ContextWindow);
        Assert.Equal(1.5, model.Cost!.Output);
        Assert.Equal("high", model.ThinkingLevelMap!["high"]);
    }

    [Fact]
    public async Task Load_LineCommentsAndBom_Stripped()
    {
        // Pinned Pi strips a BOM and // line comments (never inside strings) before
        // parsing models.json.
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "models.json");
        var content = "\uFEFF // header comment\n{\n \"providers\": { // trailing comment\n   \"p\": { \"models\": [ { \"id\": \"m\", \"api\": \"openai-completions\", \"name\": \"m // kept\" } ] }\n }\n}\n";
        await File.WriteAllTextAsync(path, content);
        var config = await ModelConfig.LoadAsync(path);
        Assert.Null(config.GetError());
        Assert.Equal("m // kept", config.GetProvider("p")!.Models![0].Name);
    }

    [Fact]
    public async Task Load_BlockComment_ParseError()
    {
        // Pinned Pi's stripJsonComments handles // line comments and trailing commas
        // only; it does not strip /* */ block comments. A block comment therefore
        // fails JSON.parse and surfaces as a parse error. Parity is deliberate: we
        // must not accept what pinned Pi rejects.
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "models.json");
        await File.WriteAllTextAsync(path, "{ /* block */ }");
        var config = await ModelConfig.LoadAsync(path);
        var error = config.GetError();
        Assert.NotNull(error);
        Assert.Contains("Failed to parse models.json", error);
    }

    [Fact]
    public void JsonText_StripsLineCommentsAndTrailingCommas_WithoutTouchingStrings()
    {
        // Direct unit tests of the stripper, independent of ModelConfig.Load.
        // Mirrors pinned Pi's regex: string literals or // line comments (pass 1),
        // string literals or trailing commas (pass 2).

        // URLs and literal comment text inside strings are preserved.
        var withUrls = JsonText.StripJsonComments("""
        { "baseUrl": "http://localhost:8000/v1", "value": "literal // not a comment" }
        """);
        var parsed = JsonNode.Parse(withUrls)!;
        Assert.Equal("http://localhost:8000/v1", parsed["baseUrl"]!.GetValue<string>());
        Assert.Equal("literal // not a comment", parsed["value"]!.GetValue<string>());

        // /* */ inside a string is preserved (it is string content, not a comment).
        var withBlockInString = JsonText.StripJsonComments("\"x/*y*/z\"");
        Assert.Equal("\"x/*y*/z\"", withBlockInString);

        // Comments before the root, between properties, and after the root.
        var placements = JsonText.StripJsonComments("""
        // before
        { "a": 1, // between
        "b": 2 } // after
        """);
        Assert.Equal(2, JsonNode.Parse(placements)!["b"]!.GetValue<int>());

        // CRLF line endings: the comment stops before \n, the leftover \r is JSON
        // whitespace.
        var crlf = JsonText.StripJsonComments("{\r\n // comment\r\n \"a\": 1\r\n}");
        Assert.Equal(1, JsonNode.Parse(crlf)!["a"]!.GetValue<int>());

        // Escaped quotes and backslashes inside strings do not end the string, so
        // a // inside such a string is preserved.
        var escaped = JsonText.StripJsonComments("{ \"a\\\"b // c\": 1, \"d\\\\e\": 2 }");
        var escapedObj = JsonNode.Parse(escaped)!;
        Assert.Equal(1, escapedObj["a\"b // c"]!.GetValue<int>());
        Assert.Equal(2, escapedObj["d\\e"]!.GetValue<int>());

        // Trailing commas are stripped (pinned Pi pass 2).
        var trailing = JsonText.StripJsonComments("{ \"a\": 1, \"b\": [1, 2,], } // tail");
        Assert.Equal(2, JsonNode.Parse(trailing)!["b"]!.AsArray().Count);

        // A block comment outside a string is left in place by pinned Pi, so it
        // becomes a parse error (covered by Load_BlockComment_ParseError).
        Assert.Equal("/* block */", JsonText.StripJsonComments("/* block */"));
    }
}
