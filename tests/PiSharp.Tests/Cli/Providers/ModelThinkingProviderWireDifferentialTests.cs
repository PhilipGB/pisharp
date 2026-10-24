using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class ModelThinkingProviderWireDifferentialTests
{
    [Fact]
    public async Task PinnedProviderRequestsMatchDirectPiLocalHttpCaptures()
    {
        var repository = FindRepositoryRoot();
        using var cases = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository,
            "tools", "parity", "fixtures", "model-thinking-provider-wire-cases-2026-10-24.json")));
        using var oracle = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository,
            "docs", "parity", "fixtures", "model-selection-thinking-provider-wire-oracle-2026-10-24.json")));
        var oracleCases = oracle.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!, StringComparer.Ordinal);

        foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var expected = ProjectRequest(oracleCases[id].GetProperty("capture"));
            var actual = await CapturePiSharpRequestAsync(item);
            var expectedNode = JsonSerializer.SerializeToNode(expected);
            var actualNode = JsonSerializer.SerializeToNode(actual);
            Assert.True(JsonNode.DeepEquals(expectedNode, actualNode),
                $"Physical request case '{id}' differs. Expected {expectedNode}; actual {actualNode}.");
            ModelThinkingDifferentialReport.Record(repository, "PISHARP_MODEL_THINKING_WIRE_REPORT_PATH",
                "model-selection-thinking-provider-wire-oracle-2026-10-24.json", id, actualNode);
        }
    }

    private static async Task<PhysicalRequest> CapturePiSharpRequestAsync(JsonElement item)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requestTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = client.GetStream();
            var requestLine = await ReadHttpLineAsync(stream, deadline.Token);
            var requestParts = requestLine.Split(' ', 3);
            Assert.Equal(3, requestParts.Length);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await ReadHttpLineAsync(stream, deadline.Token) is { Length: > 0 } headerLine)
            {
                var separator = headerLine.IndexOf(':');
                Assert.True(separator > 0, $"Malformed HTTP request header: {headerLine}");
                headers[headerLine[..separator].Trim()] = headerLine[(separator + 1)..].Trim();
            }

            if (headers.TryGetValue("Expect", out var expectation) &&
                expectation.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), deadline.Token);
            var requestBody = await ReadHttpBodyAsync(stream, headers, deadline.Token);
            var body = JsonNode.Parse(requestBody)!.AsObject();
            var requestUri = new Uri($"http://127.0.0.1{requestParts[1]}");
            var query = requestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
                    pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "", StringComparer.Ordinal);
            var responseBody = Encoding.UTF8.GetBytes(ResponseBody(item));
            var responseHeaders = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {responseBody.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders, deadline.Token);
            await stream.WriteAsync(responseBody, deadline.Token);
            return new RequestCapture(requestParts[0], requestUri.AbsolutePath, query, headers, body);
        }, deadline.Token);

        try
        {
            var api = item.GetProperty("api").GetString()!;
            var modelSource = item.GetProperty("model");
            var providerId = api == "openai-responses" ? "fixture-openai-responses" :
                modelSource.GetProperty("provider").GetString()!;
            var basePath = api.StartsWith("openai-", StringComparison.Ordinal) ? "/v1" :
                api == "anthropic-messages" ? "" : "/v1beta";
            var endpoint = new Uri($"http://127.0.0.1:{port}{basePath}");
            var model = new ModelDescriptor(
                modelSource.GetProperty("id").GetString()!, providerId, 32768, "pinned wire fixture",
                Reasoning: modelSource.GetProperty("reasoning").GetBoolean(), Provider: providerId,
                Name: modelSource.GetProperty("id").GetString(), MaxOutputTokens: modelSource.GetProperty("maxTokens").GetInt32(),
                Input: ["text"], Api: api, BaseUrl: endpoint.AbsoluteUri,
                Compatibility: ReadClone(modelSource, "compat"),
                ThinkingLevelMap: ReadClone(modelSource, "thinkingLevelMap"),
                SamplingParameters: ReadClone(modelSource, "samplingParams"),
                SamplingParametersByThinkingLevel: ReadClone(modelSource, "samplingParamsByThinkingLevel"));
            var googleVertex = api == "google-vertex"
                ? new GoogleVertexProviderOptions("fixture-project", "us-central1", null) : null;
            var profile = new ProviderProfile(providerId, providerId, endpoint, true, false, null, null, [model],
                Api: api, GoogleVertex: googleVertex);
            var selection = new ModelSelection(profile, model, "fixture-only-secret", true, "fixture credential");
            var client = ProviderChatClientFactory.Create(selection);
            var requestOptions = item.GetProperty("request");
            var chatOptions = new ChatOptions
            {
                Instructions = "fixture system instruction",
                Temperature = requestOptions.TryGetProperty("temperature", out var temperature)
                    ? (float)temperature.GetDouble() : null,
                MaxOutputTokens = requestOptions.GetProperty("maxTokens").GetInt32(),
                Reasoning = ThinkingLevels.ToOptions(requestOptions.GetProperty("reasoning").GetString()!)
            };
            chatOptions.AdditionalProperties ??= new();
            chatOptions.AdditionalProperties[ModelSamplingParametersChatClient.ThinkingLevelProperty] =
                requestOptions.GetProperty("reasoning").GetString()!;
            if (requestOptions.TryGetProperty("thinkingBudgets", out var budgets))
                chatOptions.AdditionalProperties[ThinkingBudgetPolicy.SettingsProperty] =
                    budgets.EnumerateObject().ToDictionary(property => property.Name,
                        property => property.Value.GetInt32(), StringComparer.Ordinal);
            if (requestOptions.TryGetProperty("samplingParams", out var sampling))
                chatOptions.AdditionalProperties[ModelSamplingParametersChatClient.RequestSamplingParametersProperty] = sampling.Clone();
            if (requestOptions.TryGetProperty("includeTools", out var includeTools) && includeTools.GetBoolean())
                chatOptions.Tools = [AIFunctionFactory.Create((string query) => query, name: "lookup",
                    description: "Look up a fixture value")];

            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "fixture user request")], chatOptions, deadline.Token)) { }
            var capture = await requestTask.WaitAsync(deadline.Token);
            return ProjectRequest(capture);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> ReadHttpLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        var oneByte = new byte[1];
        while (true)
        {
            var count = await stream.ReadAsync(oneByte, cancellationToken);
            if (count == 0) throw new EndOfStreamException("HTTP request ended before its headers were complete.");
            if (oneByte[0] == '\n') break;
            line.Add(oneByte[0]);
        }
        if (line.Count > 0 && line[^1] == '\r') line.RemoveAt(line.Count - 1);
        return Encoding.ASCII.GetString(line.ToArray());
    }

    private static async Task<string> ReadHttpBodyAsync(Stream stream, IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        if (headers.TryGetValue("Content-Length", out var contentLengthText) &&
            int.TryParse(contentLengthText, out var contentLength))
        {
            var body = new byte[contentLength];
            await stream.ReadExactlyAsync(body, cancellationToken);
            return Encoding.UTF8.GetString(body);
        }

        if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
            transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            using var body = new MemoryStream();
            while (true)
            {
                var chunkLine = await ReadHttpLineAsync(stream, cancellationToken);
                var separator = chunkLine.IndexOf(';');
                var sizeText = separator < 0 ? chunkLine : chunkLine[..separator];
                var size = Convert.ToInt32(sizeText, 16);
                if (size == 0)
                {
                    while ((await ReadHttpLineAsync(stream, cancellationToken)).Length > 0) { }
                    break;
                }
                var chunk = new byte[size];
                await stream.ReadExactlyAsync(chunk, cancellationToken);
                body.Write(chunk);
                _ = await ReadHttpLineAsync(stream, cancellationToken);
            }
            return Encoding.UTF8.GetString(body.ToArray());
        }

        throw new InvalidDataException("HTTP request did not declare its body length.");
    }

    private static PhysicalRequest ProjectRequest(RequestCapture request)
    {
        var api = request.Path.EndsWith("/responses", StringComparison.Ordinal) ? "openai-responses" :
            request.Path.EndsWith("/chat/completions", StringComparison.Ordinal) ? "openai-completions" :
            request.Path.EndsWith("/messages", StringComparison.Ordinal) ? "anthropic-messages" :
            request.Path.Contains("publishers/google", StringComparison.Ordinal) ? "google-vertex" : "google-generative-ai";
        var headers = request.Headers;
        var credentialHeader = headers.ContainsKey("authorization") ? "authorization" :
            headers.ContainsKey("x-api-key") ? "x-api-key" : headers.ContainsKey("x-goog-api-key") ? "x-goog-api-key" : null;
        var model = request.Body["model"]?.GetValue<string>() ??
            request.Path.Split("/models/", StringSplitOptions.None).LastOrDefault()?.Split(':')[0] ?? "";
        var context = ReadContext(api, request.Body);
        var maxOutputTokens = api switch
        {
            "openai-responses" => ReadInt(request.Body["max_output_tokens"]),
            "openai-completions" => ReadInt(request.Body["max_completion_tokens"] ?? request.Body["max_tokens"]),
            "anthropic-messages" => ReadInt(request.Body["max_tokens"]),
            _ => ReadInt(request.Body["generationConfig"]?["maxOutputTokens"])
        };
        var thinking = ReadThinking(api, request.Body);
        var sampling = ReadSampling(api, request.Body);
        var tools = ReadTools(api, request.Body);
        var streaming = api.StartsWith("google-", StringComparison.Ordinal) || request.Body["stream"]?.GetValue<bool>() == true;
        var query = new SortedDictionary<string, string>(request.Query.ToDictionary(item => item.Key,
            item => item.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        return new PhysicalRequest(api, request.Method, request.Path, query, credentialHeader, model, context,
            maxOutputTokens, streaming, thinking, sampling, tools);
    }

    private static PhysicalRequest ProjectRequest(JsonElement capture)
    {
        var body = JsonNode.Parse(capture.GetProperty("body").GetRawText())!.AsObject();
        var headers = capture.GetProperty("headers").EnumerateObject().ToDictionary(property =>
            property.Name.ToLowerInvariant(), property => property.Value.GetString()!, StringComparer.Ordinal);
        var query = capture.GetProperty("query").EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.GetString()!, StringComparer.Ordinal);
        return ProjectRequest(new RequestCapture(capture.GetProperty("method").GetString()!,
            capture.GetProperty("path").GetString()!, query, headers, body));
    }

    private static JsonArray ReadContext(string api, JsonObject body)
    {
        var result = new JsonArray();
        if (api is "openai-responses")
        {
            if (body["instructions"] is JsonValue instructions && instructions.TryGetValue<string>(out var systemText))
                result.Add(ContextItem("system", systemText));
            if (body["input"] is JsonArray input)
                foreach (var message in input.OfType<JsonObject>())
                    result.Add(ContextItem(message["role"]?.GetValue<string>() == "developer" ? "system" :
                        message["role"]?.GetValue<string>() ?? "user", ReadText(message["content"])));
        }
        else if (api is "openai-completions")
        {
            if (body["messages"] is JsonArray messages)
                foreach (var message in messages.OfType<JsonObject>())
                    result.Add(ContextItem(message["role"]?.GetValue<string>() == "developer" ? "system" :
                        message["role"]?.GetValue<string>() ?? "user", ReadText(message["content"])));
        }
        else if (api is "anthropic-messages")
        {
            if (body["system"] is JsonArray system)
                result.Add(ContextItem("system", ReadText(system)));
            if (body["messages"] is JsonArray messages)
                foreach (var message in messages.OfType<JsonObject>())
                    result.Add(ContextItem(message["role"]?.GetValue<string>() ?? "user", ReadText(message["content"])));
        }
        else
        {
            if (body["systemInstruction"] is JsonObject instruction)
                result.Add(ContextItem("system", ReadText(instruction["parts"])));
            if (body["contents"] is JsonArray contents)
                foreach (var message in contents.OfType<JsonObject>())
                    result.Add(ContextItem(message["role"]?.GetValue<string>() == "model" ? "assistant" : "user",
                        ReadText(message["parts"])));
        }
        return result;
    }

    private static JsonObject ContextItem(string role, string text) => new() { ["role"] = role, ["text"] = text };

    private static string ReadText(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return text;
        if (value is JsonArray array)
            return string.Concat(array.OfType<JsonObject>().Select(ReadText));
        if (value is JsonObject obj)
        {
            if (obj["text"] is JsonValue blockText && blockText.TryGetValue<string>(out var blockValue)) return blockValue;
            if (obj["parts"] is JsonNode parts) return ReadText(parts);
        }
        return "";
    }

    private static JsonObject ReadThinking(string api, JsonObject body)
    {
        var result = new JsonObject();
        if (api == "openai-responses" && body["reasoning"] is JsonObject responses)
            result["effort"] = responses["effort"]?.DeepClone();
        else if (api == "openai-completions") result["effort"] = body["reasoning_effort"]?.DeepClone();
        else if (api == "anthropic-messages" && body["thinking"] is JsonObject anthropic)
        {
            result["mode"] = anthropic["type"]?.DeepClone();
            result["budget"] = anthropic["budget_tokens"]?.DeepClone();
        }
        else if (body["generationConfig"]?["thinkingConfig"] is JsonObject google)
        {
            result["level"] = google["thinkingLevel"]?.DeepClone();
            result["budget"] = google["thinkingBudget"]?.DeepClone();
            result["includeThoughts"] = google["includeThoughts"]?.DeepClone();
        }
        return result;
    }

    private static JsonObject ReadSampling(string api, JsonObject body)
    {
        var source = api.StartsWith("google-", StringComparison.Ordinal)
            ? body["generationConfig"] as JsonObject : body;
        var result = new JsonObject();
        foreach (var key in new[] { "temperature", "top_p", "top_k", "min_p" })
            if (source?[key] is { } value) result[key] = value.DeepClone();
        return result;
    }

    private static JsonArray ReadTools(string api, JsonObject body)
    {
        var result = new JsonArray();
        JsonArray? tools = body["tools"] as JsonArray;
        if (tools is null) return result;
        foreach (var tool in tools.OfType<JsonObject>())
        {
            JsonObject? function = api switch
            {
                "openai-completions" => tool["function"] as JsonObject,
                "google-generative-ai" or "google-vertex" => (tool["functionDeclarations"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(),
                _ => tool
            };
            if (function is null) continue;
            var schema = function["parameters"] ?? function["parametersJsonSchema"] ?? function["input_schema"];
            var properties = schema?["properties"] is JsonObject propertyObject
                ? new JsonArray(propertyObject.Select(property => JsonValue.Create(property.Key)).ToArray()) : new JsonArray();
            var required = schema?["required"] is JsonArray requiredArray
                ? new JsonArray(requiredArray.Select(item => item?.DeepClone()).ToArray()) : new JsonArray();
            result.Add(new JsonObject
            {
                ["name"] = function["name"]?.DeepClone(),
                ["properties"] = properties,
                ["required"] = required
            });
        }
        return result;
    }

    private static int? ReadInt(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<int>(out var number)
        ? number : null;

    private static JsonElement? ReadClone(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? value.Clone() : null;

    private static string ResponseBody(JsonElement item)
    {
        var api = item.GetProperty("api").GetString();
        return api switch
        {
            "openai-responses" => "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_fixture\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture\",\"status\":\"in_progress\",\"output\":[]}}\n\n" +
                                  "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"item_fixture\",\"output_index\":0,\"content_index\":0,\"delta\":\"ok\"}\n\n" +
                                  "data: [DONE]\n\n",
            "openai-completions" => "data: {\"id\":\"chatcmpl_fixture\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\n" +
                                     "data: {\"id\":\"chatcmpl_fixture\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}\n\n" +
                                     "data: [DONE]\n\n",
            "anthropic-messages" => "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_fixture\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"fixture\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}\n\n" +
                                     "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
                                     "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"ok\"}}\n\n" +
                                     "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
                                     "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":1}}\n\n" +
                                     "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
            _ => "data: {\"responseId\":\"google_fixture\",\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}\n\n"
        };
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate PiSharp.slnx from the test output directory.");
    }

    private sealed record RequestCapture(string Method, string Path, IReadOnlyDictionary<string, string> Query,
        IReadOnlyDictionary<string, string> Headers, JsonObject Body);

    private sealed record PhysicalRequest(string Api, string Method, string Path,
        SortedDictionary<string, string> Query, string? CredentialHeader, string Model, JsonArray Context,
        int? MaxOutputTokens, bool Streaming, JsonObject Thinking, JsonObject Sampling, JsonArray Tools);
}
