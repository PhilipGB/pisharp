using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime;

namespace PiSharp.Tests;

public sealed class AnthropicStrictToolFallbackTests
{
    internal const string GrammarError = "The compiled grammar is too large, which would cause performance issues.";
    private static readonly string[] s_limits = [GrammarError, "Too many strict tools (32). Maximum 20.",
        "Schemas contains too many parameters with union types (480 parameters)."];

    public static IEnumerable<object[]> CapacityCases => s_limits.SelectMany(message =>
        new[] { new object[] { message, false }, [message, true] });

    [Theory]
    [MemberData(nameof(CapacityCases))]
    public async Task CapacityRejectionRetriesPreferredOriginalSchemasAndRetainsRequiredTools(string message, bool streaming)
    {
        using var harness = new AnthropicStrictHttpFixture((_, attempt) => attempt == 1 ? Failure(message) : Success);
        var messages = Messages();
        var original = JsonSerializer.Serialize(messages, AIJsonUtilities.DefaultOptions);
        using var client = harness.Client();
        var response = await InvokeAsync(client, messages, streaming, harness.Token);
        Assert.Equal("ok", response.Text);
        Assert.True(Assert.Single(response.Messages).AdditionalProperties?.ContainsKey("diagnostics"));
        await harness.StopAsync();
        Assert.Equal(2, harness.Payloads.Count);
        var first = harness.Payloads[0].GetProperty("tools");
        var second = harness.Payloads[1].GetProperty("tools");
        Assert.True(first[0].GetProperty("strict").GetBoolean());
        Assert.Equal(new[] { "value", "optional" }, first[0].GetProperty("input_schema").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("null", first[0].GetProperty("input_schema").GetProperty("properties").GetProperty("optional").GetProperty("anyOf")[1].GetProperty("type").GetString());
        Assert.False(second[0].TryGetProperty("strict", out _));
        Assert.Equal(new[] { "value" }, second[0].GetProperty("input_schema").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("number", second[0].GetProperty("input_schema").GetProperty("properties").GetProperty("optional").GetProperty("type").GetString());
        Assert.True(JsonElement.DeepEquals(first[1], second[1]));
        Assert.True(first[1].GetProperty("strict").GetBoolean());
        Assert.True(JsonElement.DeepEquals(first[2], second[2]));
        Assert.Equal(original, JsonSerializer.Serialize(messages, AIJsonUtilities.DefaultOptions));
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("require")]
    [InlineData("unsupported-prefer")]
    [InlineData("unrelated-message")]
    [InlineData("wrong-type")]
    [InlineData("wrong-status")]
    public async Task OtherRejectionsDoNotDowngradeOrRetry(string scenario)
    {
        var messages = Messages(scenario == "ordinary" ? null : scenario == "require" ? "require" : "prefer", required: false);
        if (scenario == "unsupported-prefer")
        {
            var tool = Definition("preferred", "prefer");
            tool["parameters"]!["properties"]!["optional"]!["minimum"] = 1;
            messages = AnthropicInlineToolTests.Import(new JsonObject
            { ["role"] = "system", ["content"] = "base", ["toolsAdded"] = new JsonArray(tool) }.ToJsonString() +
                "\n{\"role\":\"user\",\"content\":\"Hello\"}").ContextMessages().ToArray();
        }
        using var harness = new AnthropicStrictHttpFixture((_, _) => Failure(scenario == "unrelated-message" ? "Invalid tool schema" : GrammarError,
            scenario == "wrong-status" ? 500 : 400, scenario == "wrong-type" ? "authentication_error" : "invalid_request_error"));
        using var client = harness.Client();
        await Assert.ThrowsAnyAsync<AnthropicApiException>(() => InvokeAsync(client, messages, true, harness.Token));
        await harness.StopAsync();
        Assert.Single(harness.Payloads);
        if (scenario is "ordinary" or "unsupported-prefer")
            Assert.False(harness.Payloads[0].GetProperty("tools")[0].TryGetProperty("strict", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondRejectionIsSurfacedWithoutAThirdAttempt(bool streaming)
    {
        using var harness = new AnthropicStrictHttpFixture((_, _) => Failure());
        using var client = harness.Client();
        var error = await Assert.ThrowsAnyAsync<AnthropicApiException>(() => InvokeAsync(client, Messages(), streaming, harness.Token));
        Assert.True(error.Data.Contains("diagnostics"));
        await harness.StopAsync();
        Assert.Equal(2, harness.Payloads.Count);
        Assert.True(harness.Payloads[1].GetProperty("tools")[1].GetProperty("strict").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PayloadHooksRunAgainAndStrictPolicyResetsForTheNextRequest(bool streaming)
    {
        using var harness = new AnthropicStrictHttpFixture((_, attempt) => attempt == 1 ? Failure() : Success);
        using var client = harness.Client();
        var hooks = 0;
        var options = new ChatOptions
        {
            RawRepresentationFactory = _ =>
        {
            hooks++;
            return new MessageCreateParams { Model = "fixture", MaxTokens = 123, Messages = [] };
        }
        };
        Assert.Equal("ok", (await InvokeAsync(client, Messages(), streaming, harness.Token, options)).Text);
        Assert.Equal("ok", (await InvokeAsync(client, Messages(), streaming, harness.Token, options)).Text);
        await harness.StopAsync();
        Assert.Equal(3, hooks);
        Assert.Equal(3, harness.Payloads.Count);
        Assert.True(harness.Payloads[0].GetProperty("tools")[0].GetProperty("strict").GetBoolean());
        Assert.False(harness.Payloads[1].GetProperty("tools")[0].TryGetProperty("strict", out _));
        Assert.True(harness.Payloads[2].GetProperty("tools")[0].GetProperty("strict").GetBoolean());
        Assert.All(harness.Payloads, payload => Assert.Equal(123, payload.GetProperty("max_tokens").GetInt32()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HookExceptionsThatLookLikeCapacityRejectionsAreNotRetried(bool streaming)
    {
        using var harness = new AnthropicStrictHttpFixture((_, _) => Success);
        using var client = harness.Client();
        var hooks = 0;
        var options = new ChatOptions
        {
            RawRepresentationFactory = _ =>
        {
            hooks++;
            throw new AnthropicBadRequestException { StatusCode = HttpStatusCode.BadRequest, ResponseBody = Failure().Body };
        }
        };
        await Assert.ThrowsAnyAsync<AnthropicApiException>(() => InvokeAsync(client, Messages(), streaming, harness.Token, options));
        await harness.StopAsync();
        Assert.Equal(1, hooks);
        Assert.Empty(harness.Payloads);
    }

    [Fact]
    public async Task CancellationAfterARejectedRequestPreventsFallback()
    {
        using var cancellation = new CancellationTokenSource();
        using var harness = new AnthropicStrictHttpFixture((_, _) => { cancellation.Cancel(); return Failure(); }, ignoreDisconnect: true);
        using var client = harness.Client();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(client, Messages(), true, cancellation.Token));
        await harness.StopAsync();
        Assert.Single(harness.Payloads);
    }

    [Fact]
    public async Task RejectionInsideAnAcceptedStreamDoesNotReplay()
    {
        using var harness = new AnthropicStrictHttpFixture((_, _) => (200, "event: error\ndata: " + Failure().Body + "\n\n"));
        using var client = harness.Client();
        await Assert.ThrowsAnyAsync<Exception>(() => InvokeAsync(client, Messages(), true, harness.Token));
        await harness.StopAsync();
        Assert.Single(harness.Payloads);
    }

    [Fact]
    public async Task InlineSameNamePreferredAndRequiredDefinitionsKeepTheirOwnPolicy()
    {
        var initial = Definition("lookup", "require");
        var preferred = Definition("lookup", "prefer");
        var messages = AnthropicInlineToolTests.Import(string.Join('\n', new[]
        {
            new JsonObject { ["role"] = "system", ["content"] = "base", ["toolsAdded"] = new JsonArray(initial) }.ToJsonString(),
            "{\"role\":\"user\",\"content\":\"Hello\"}",
            new JsonObject { ["role"] = "system", ["content"] = "", ["toolsAdded"] = new JsonArray(preferred) }.ToJsonString(),
            new JsonObject { ["role"] = "system", ["content"] = "", ["toolsAdded"] = new JsonArray(initial.DeepClone()) }.ToJsonString()
        })).ContextMessages().ToArray();
        using var harness = new AnthropicStrictHttpFixture((_, attempt) => attempt == 1 ? Failure() : Success);
        using var client = harness.Client(nativeChanges: true);
        Assert.Equal("ok", (await InvokeAsync(client, messages, true, harness.Token)).Text);
        await harness.StopAsync();
        Assert.Equal(2, harness.Payloads.Count);
        Assert.True(JsonElement.DeepEquals(harness.Payloads[0].GetProperty("tools"), harness.Payloads[1].GetProperty("tools")));
        JsonElement[] Additions(JsonElement payload) => payload.GetProperty("messages").EnumerateArray()
            .SelectMany(message => message.GetProperty("content").EnumerateArray()).Where(block => block.GetProperty("type").GetString() == "tool_addition")
            .Select(block => block.GetProperty("tool").GetProperty("definition")).ToArray();
        var first = Additions(harness.Payloads[0]);
        var second = Additions(harness.Payloads[1]);
        Assert.True(first[0].GetProperty("strict").GetBoolean());
        Assert.False(second[0].TryGetProperty("strict", out _));
        Assert.True(JsonElement.DeepEquals(first[1], second[1]));
    }

    [Fact]
    public async Task LiveExtensionPolicySurvivesToolTranscriptAndNativeAndPiPersistence()
    {
        using var harness = new AnthropicStrictHttpFixture((_, attempt) => attempt == 1 ? Failure() : Success);
        using var client = harness.Client();
        var registration = new ExtensionRegistration();
        var policy = JsonSerializer.SerializeToElement(new { type = "json_schema", strict = "prefer" });
        registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create((string value, double? optional) => value,
            name: "preferred"), ConstrainedSampling: policy));
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            extensionToolRegistrations: registration.ToolDefinitions);
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(agent, session);
        await foreach (var _ in run.RunEventsAsync("Hello", harness.Token)) { }
        await harness.StopAsync();
        Assert.Equal(2, harness.Payloads.Count);
        Assert.True(harness.Payloads[0].GetProperty("tools")[0].GetProperty("strict").GetBoolean());
        var exported = PiJsonlSessionInterchange.Export(session);
        Assert.Contains("constrainedSampling", exported);
        var native = ConversationSession.Parse(session.ToJson());
        Assert.Contains("constrainedSampling", PiJsonlSessionInterchange.Export(native));
        var restored = PiJsonlSessionInterchange.Import(exported);
        Assert.Contains("constrainedSampling", PiJsonlSessionInterchange.Export(restored));
    }

    [Fact]
    public void PiMessagesForwardsTheOriginalPreferredSchemaAndPolicy()
    {
        var policy = JsonSerializer.SerializeToElement(new { type = "json_schema", strict = "prefer" });
        var function = AIFunctionFactory.Create((string value, double? optional) => value,
            new AIFunctionFactoryOptions { Name = "preferred", AdditionalProperties = new Dictionary<string, object?> { ["constrainedSampling"] = policy } });
        var payload = PiMessagesRequestMapper.Build("fixture", [], new ChatOptions { Tools = [function] });
        var definition = JsonSerializer.SerializeToElement(payload).GetProperty("context")
            .GetProperty("messages")[0].GetProperty("toolsAdded")[0];
        Assert.True(JsonElement.DeepEquals(policy, definition.GetProperty("constrainedSampling")));
        Assert.True(JsonElement.DeepEquals(function.JsonSchema, definition.GetProperty("parameters")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredUnsupportedConstraintsFailBeforeAnyHttpRequest(bool supported)
    {
        using var harness = new AnthropicStrictHttpFixture((_, _) => Success);
        using var client = harness.Client(strictSupport: supported);
        var tool = Definition("required", "require");
        if (supported) tool["parameters"]!["properties"]!["optional"]!["minimum"] = 1;
        var messages = AnthropicInlineToolTests.Import(new JsonObject
        { ["role"] = "system", ["content"] = "base", ["toolsAdded"] = new JsonArray(tool) }.ToJsonString() +
            "\n{\"role\":\"user\",\"content\":\"Hello\"}").ContextMessages().ToArray();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(client, messages, false, harness.Token));
        Assert.Contains(supported ? "minimum: 1 is unsupported" : "strict tools are unsupported", error.Message);
        await harness.StopAsync();
        Assert.Empty(harness.Payloads);
    }

    [Fact]
    public async Task LegacyThinkingUsesConfiguredBudgetAndExpandsTheOutputCeiling()
    {
        using var harness = new AnthropicStrictHttpFixture((_, _) => Success);
        using var client = harness.Client(reasoning: true, maxOutputTokens: 20_000);
        var options = new ChatOptions
        {
            MaxOutputTokens = 4096,
            Reasoning = ThinkingLevels.ToOptions("medium")
        };
        options.AdditionalProperties ??= new();
        options.AdditionalProperties[ThinkingBudgetPolicy.SettingsProperty] =
            new Dictionary<string, int>(StringComparer.Ordinal) { ["medium"] = 5000 };

        Assert.Equal("ok", (await InvokeAsync(client, Messages(), streaming: false, harness.Token, options)).Text);
        await harness.StopAsync();

        var payload = Assert.Single(harness.Payloads);
        Assert.Equal(9096, payload.GetProperty("max_tokens").GetInt32());
        Assert.Equal(5000, payload.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.False(payload.TryGetProperty(ThinkingBudgetPolicy.SettingsProperty, out _));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("2", false)]
    [InlineData("null", false)]
    public void ArrayMinimumUsesNumericValueWhenDecidingStrictSupport(string minimum, bool expected)
    {
        var tool = Definition("preferred", "prefer");
        tool["parameters"]!["properties"]!["optional"] = JsonNode.Parse(
            "{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":" + minimum + "}");
        var projection = new PiSharp.Cli.AnthropicToolDefinitionProjection(true, true, name => name, false);
        var definition = projection.Create(JsonSerializer.SerializeToElement(tool));
        Assert.Equal(expected, definition["strict"]?.GetValue<bool>() == true);
    }

    internal static ChatMessage[] Messages(string? policy = "prefer", bool required = true)
    {
        var definitions = new JsonArray { Definition("preferred", policy) };
        if (required) definitions.Add(Definition("required", "require"));
        definitions.Add(Definition("ordinary", null));
        return AnthropicInlineToolTests.Import(JsonSerializer.Serialize(new
        {
            role = "system",
            content = "base",
            toolsAdded = definitions
        }) + "\n" + "{\"role\":\"user\",\"content\":\"Hello\"}").ContextMessages().ToArray();
    }

    private static JsonObject Definition(string name, string? policy)
    {
        var result = JsonNode.Parse("""
            {"name":"","description":"lookup","parameters":{"type":"object","properties":{"value":{"type":"string"},"optional":{"type":"number"}},"required":["value"]}}
            """)!.AsObject();
        result["name"] = name;
        if (policy is not null) result["constrainedSampling"] = new JsonObject { ["type"] = "json_schema", ["strict"] = policy };
        return result;
    }

    internal static Task<ChatResponse> InvokeAsync(IChatClient client, ChatMessage[] messages, bool streaming,
        CancellationToken token, ChatOptions? options = null) => streaming
        ? client.GetStreamingResponseAsync(messages, options, token).ToChatResponseAsync(token)
        : client.GetResponseAsync(messages, options, token);

    internal static (int Status, string Body) Failure(string message = GrammarError, int status = 400,
        string type = "invalid_request_error") => (status,
            JsonSerializer.Serialize(new { type = "error", error = new { type, message } }));

    internal static readonly (int Status, string Body) Success = (200, """
        {"id":"msg_fixture","type":"message","role":"assistant","model":"fixture","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":4,"output_tokens":2}}
        """);

}
