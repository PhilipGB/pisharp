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
    public async Task Load_InvalidJson_DeterministicError()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "models.json");
        await File.WriteAllTextAsync(path, "{ not json");
        var config = await ModelConfig.LoadAsync(path);
        Assert.NotNull(config.GetError());
        Assert.Contains("Invalid models.json schema", config.GetError());
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
    public async Task Load_InvalidApi_Error()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "models.json");
        await File.WriteAllTextAsync(path, """
        {
          "providers": {
            "p": { "models": [ { "id": "m", "api": "bogus-api" } ] }
          }
        }
        """);
        var config = await ModelConfig.LoadAsync(path);
        var error = config.GetError();
        Assert.NotNull(error);
        Assert.Contains("must be equal to one of the allowed values", error);
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
    public async Task Load_CommentsAndBom_Stripped()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "models.json");
        var content = "\uFEFF{\n // line comment\n \"providers\": {\n   \"p\": { \"models\": [ { \"id\": \"m\", \"api\": \"openai-completions\", /* inline */ \"name\": \"m\" } ] }\n }\n}\n";
        await File.WriteAllTextAsync(path, content);
        var config = await ModelConfig.LoadAsync(path);
        Assert.Null(config.GetError());
        Assert.Equal("m", config.GetProvider("p")!.Models![0].Id);
    }

    [Fact]
    public void JsonText_StripsCommentsWithoutTouchingStrings()
    {
        var json = """
        {
          "a": "keep $ and // inside",
          "b": "x/*y*/z",
          "c": 1
        } // trailing
        /* block */
        """;
        var stripped = JsonText.StripJsonComments(json);
        var parsed = JsonNode.Parse(stripped);
        Assert.Equal("keep $ and // inside", parsed!["a"]!.GetValue<string>());
        Assert.Equal("x/*y*/z", parsed["b"]!.GetValue<string>());
        Assert.Equal(1, parsed["c"]!.GetValue<int>());
    }
}
