using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

internal static class C11Oracle
{
    private const string CustomInstructions = "preserve release decisions";
    private const string Summary = "compacted release summary";
    private const string Continuation = "continue after compaction";

    public static async Task<object> RunAsync(string workingDirectory)
    {
        var rpcDirectory = Path.Combine(workingDirectory, "c11-rpc");
        Directory.CreateDirectory(rpcDirectory);
        var conversation = CreateConversation(rpcDirectory, includeLargeHistory: false);
        var tuiObservation = await RunInteractiveCompactAsync(Path.Combine(workingDirectory, "c11-tui"));
        var summaryClient = new CaptureClient(Summary, "continued response");
        var run = await OpenRunAsync(conversation, rpcDirectory, summaryClient, keepRecentTokens: 1);
        using var rpcOutput = new StringWriter();
        await new RpcMode(new StringReader(JsonSerializer.Serialize(new
        {
            id = "compact-c11",
            type = "compact",
            customInstructions = CustomInstructions
        })), rpcOutput, run).ServeAsync();

        var rpcRecords = ParseLines(rpcOutput.ToString());
        var rpcResponse = rpcRecords.Single(record =>
            record.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
            id.GetString() == "compact-c11");
        var compactionStart = rpcRecords.Single(record =>
            record.TryGetProperty("type", out var type) && type.GetString() == "compaction_start");
        var compactionEnd = rpcRecords.Single(record =>
            record.TryGetProperty("type", out var type) && type.GetString() == "compaction_end");
        var result = rpcResponse.GetProperty("data");
        var context = conversation.ContextMessages().Select(MessageSnapshot).ToArray();
        var retainedEntries = conversation.Tree.ActivePath().Select(entry => entry.Payload.GetRawText()).ToArray();

        using var stateOutput = new StringWriter();
        await new RpcMode(new StringReader(
            "{\"id\":\"state-c11\",\"type\":\"get_state\"}\n" +
            "{\"id\":\"stats-c11\",\"type\":\"get_session_stats\"}"), stateOutput, run).ServeAsync();
        var stateRecords = ParseLines(stateOutput.ToString());
        var state = stateRecords.Single(record =>
            record.TryGetProperty("id", out var id) && id.GetString() == "state-c11").GetProperty("data");
        var stats = stateRecords.Single(record =>
            record.TryGetProperty("id", out var id) && id.GetString() == "stats-c11").GetProperty("data");

        var providerRequests = new List<object>();
        foreach (var api in new[] { "openai-responses", "openai-completions", "anthropic-messages" })
        {
            var request = await CaptureProviderRequestAsync(api,
                [.. conversation.ContextMessages(), new ChatMessage(ChatRole.User, Continuation)],
                Path.Combine(workingDirectory, "c11-provider-" + api));
            providerRequests.Add(request);
        }

        var jsonMode = await RunJsonModeCompactionAsync(Path.Combine(workingDirectory, "c11-json"));
        var resultFields = result.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        var statusEvents = rpcRecords.Where(record => record.TryGetProperty("type", out var type) &&
                type.GetString() is "compaction_start" or "compaction_end")
            .Select(record => record.GetProperty("type").GetString()).ToArray();
        long? usageTokens = stats.TryGetProperty("tokens", out var tokenStats) &&
            tokenStats.TryGetProperty("total", out var totalTokens) && totalTokens.ValueKind == JsonValueKind.Number
                ? totalTokens.GetInt64() : null;
        var messages = conversation.ActiveMessages().Select(message => message.Text).ToArray();

        return new
        {
            cases = new Dictionary<string, object>
            {
                ["C11.cli-manual-compact-input-and-result"] = new
                {
                    command = "/compact " + CustomInstructions,
                    customInstructionsForwarded = tuiObservation.CustomInstructionsForwarded,
                    succeeded = tuiObservation.Succeeded,
                    summary = tuiObservation.Summary
                },
                ["C11.tui-compact-command-and-progress-status"] = new
                {
                    commandAccepted = tuiObservation.Succeeded,
                    progressStarted = tuiObservation.ProgressStarted,
                    progressCleared = tuiObservation.ProgressCleared,
                    manualReason = compactionStart.GetProperty("reason").GetString()
                },
                ["C11.rpc-compact-request-and-result-shape"] = new
                {
                    id = rpcResponse.GetProperty("id").GetString(),
                    command = rpcResponse.GetProperty("command").GetString(),
                    success = rpcResponse.GetProperty("success").GetBoolean(),
                    resultFields,
                    usageTotalTokens = result.TryGetProperty("usage", out var resultUsage) &&
                        resultUsage.TryGetProperty("totalTokens", out var resultTotal) && resultTotal.ValueKind == JsonValueKind.Number
                            ? resultTotal.GetInt64() : (long?)null
                },
                ["C11.rpc-event-order-and-correlation"] = new
                {
                    eventOrder = statusEvents,
                    responseId = rpcResponse.GetProperty("id").GetString(),
                    startReason = compactionStart.GetProperty("reason").GetString(),
                    endReason = compactionEnd.GetProperty("reason").GetString()
                },
                ["C11.rpc-state-context-usage-after-compaction"] = new
                {
                    isCompacting = state.GetProperty("isCompacting").GetBoolean(),
                    summaryInContext = context.Any(message => message.Contains(Summary, StringComparison.Ordinal)),
                    contextMessageCount = context.Length,
                    usageTotalTokens = usageTokens
                },
                ["C11.json-mode-compaction-output-contract"] = jsonMode,
                ["C11.responses-wire-request-after-compaction"] = providerRequests[0],
                ["C11.chat-completions-wire-request-after-compaction"] = providerRequests[1],
                ["C11.anthropic-messages-wire-request-after-compaction"] = providerRequests[2],
                ["C11.provider-history-is-valid-and-summary-not-canonical-projection"] = new
                {
                    providerContextContainsSummary = context.Any(message => message.Contains(Summary, StringComparison.Ordinal)),
                    providerContextContainsRetainedHistory = context.Any(message => message.Contains("RETAINED ANSWER", StringComparison.Ordinal)),
                    providerContextDropsOldHistory = context.All(message => !message.Contains("OLDER ANSWER", StringComparison.Ordinal)),
                    canonicalHistoryRetainsOldEntries = retainedEntries.Any(entry => entry.Contains("OLDER ANSWER", StringComparison.Ordinal)),
                    outboundHistoryValid = IsBalancedToolHistory(context),
                    canonicalMessageCount = messages.Length
                }
            }
        };
    }

    private static async Task<TuiCompactObservation> RunInteractiveCompactAsync(string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        var agentDirectory = Path.Combine(workingDirectory, "agent");
        var sessionDirectory = Path.Combine(workingDirectory, "sessions");
        Directory.CreateDirectory(agentDirectory);
        var listenerBaseUrl = StartListenerAsync(out var listener, out _);
        var endpoint = new Uri(listenerBaseUrl + "v1/");
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
        {
            providers = new
            {
                fixture = new
                {
                    baseUrl = endpoint.ToString(),
                    apiKeyEnv = "PISHARP_C11_FIXTURE_KEY",
                    models = new[] { new { id = "fixture-model", api = "openai-completions" } }
                }
            }
        }));
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "settings.json"),
            "{\"compaction\":{\"keepRecentTokens\":1},\"terminal\":{\"showTerminalProgress\":true}}\n");

        var conversation = CreateConversation(workingDirectory, includeLargeHistory: false, endpoint.ToString(), "fixture");
        var store = new ConversationStore(workingDirectory, sessionDirectory);
        var sessionPath = store.NewPath(conversation);
        await store.SaveAsync(conversation, sessionPath);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var requestReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Task.Run(async () =>
        {
            for (var requestNumber = 0; requestNumber < 2; requestNumber++)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                string body;
                using (var reader = new StreamReader(context.Request.InputStream))
                    body = await reader.ReadToEndAsync(timeout.Token);
                if (requestNumber == 0) requestReceived.TrySetResult(body);
                await releaseProvider.Task.WaitAsync(timeout.Token);
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.ContentType = "application/json";
                await using (var writer = new StreamWriter(context.Response.OutputStream))
                    await writer.WriteAsync(ProviderResponse("openai-completions",
                        requestNumber == 0 ? Summary : "continued response"));
                context.Response.Close();
            }
        }, timeout.Token);

        Process? process = null;
        try
        {
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"stty rows 24 cols 100; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --provider fixture --model fixture-model --offline --session {ShellQuote(sessionPath)} --session-dir {ShellQuote(sessionDirectory)} --no-tools");
            start.ArgumentList.Add("/dev/null");
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_C11_FIXTURE_KEY" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_SESSION_DIR"] = sessionDirectory;
            start.Environment["PISHARP_C11_FIXTURE_KEY"] = "fixture-only-key";
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start PiSharp TUI compaction fixture.");

            var output = new StringBuilder();
            var outputGate = new object();
            var inputReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var compactStatusVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var compactionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[2048];
                while (await process.StandardOutput.ReadAsync(buffer).ConfigureAwait(false) is var count && count > 0)
                {
                    lock (outputGate)
                    {
                        output.Append(buffer, 0, count);
                        var captured = output.ToString();
                        if (captured.Contains("\u001b[?2004h", StringComparison.Ordinal)) inputReady.TrySetResult();
                        if (captured.Contains("Compacting context...", StringComparison.Ordinal)) compactStatusVisible.TrySetResult();
                        if (captured.Contains("Context compacted; full history retained.", StringComparison.Ordinal))
                            compactionCompleted.TrySetResult();
                    }
                }
                lock (outputGate) return output.ToString();
            }, timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

            try { await inputReady.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                string startupOutput;
                lock (outputGate) startupOutput = output.ToString();
                throw new InvalidOperationException($"PiSharp TUI did not reach its interactive prompt. exited={process.HasExited}; stdout: {startupOutput}; stderr: {await stderr}");
            }
            await process.StandardInput.WriteAsync("/compact " + CustomInstructions + "\r").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            var requestBody = await requestReceived.Task.WaitAsync(timeout.Token);
            await compactStatusVisible.Task.WaitAsync(timeout.Token);
            releaseProvider.TrySetResult();
            await provider.WaitAsync(timeout.Token);
            try { await compactionCompleted.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                string currentOutput;
                lock (outputGate) currentOutput = output.ToString();
                throw new InvalidOperationException($"PiSharp TUI did not report compaction completion. exited={process.HasExited}; stdout: {currentOutput}");
            }

            await process.StandardInput.WriteAsync("/quit\r").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var captured = await stdout.WaitAsync(timeout.Token);
            var stderrText = await stderr;
            if (process.ExitCode != 0 || stderrText.Contains("Exception:", StringComparison.Ordinal))
                throw new InvalidOperationException($"PiSharp TUI compaction failed (exit {process.ExitCode}): {stderrText}\n{captured}");

            var reloaded = await store.LoadAsync(sessionPath, timeout.Token);
            var summary = reloaded.Tree.ActivePath().Single(entry => entry.Type == "compaction")
                .Payload.GetProperty("summary").GetString();
            var started = captured.IndexOf("\u001b]9;4;3\u0007", StringComparison.Ordinal);
            var cleared = started >= 0 && captured.IndexOf("\u001b]9;4;0\u0007", started, StringComparison.Ordinal) > started;
            return new TuiCompactObservation(
                requestBody.Contains(CustomInstructions, StringComparison.Ordinal),
                captured.Contains("Context compacted; full history retained.", StringComparison.Ordinal),
                captured.Contains("Compacting context...", StringComparison.Ordinal) && started >= 0,
                cleared,
                summary);
        }
        finally
        {
            releaseProvider.TrySetResult();
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            listener.Close();
        }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed record TuiCompactObservation(bool CustomInstructionsForwarded, bool Succeeded,
        bool ProgressStarted, bool ProgressCleared, string? Summary);

    private static async Task<object> RunJsonModeCompactionAsync(string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        var conversation = CreateConversation(workingDirectory, includeLargeHistory: true);
        var client = new CaptureClient("json compacted summary", "json continued response");
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(workingDirectory), noTools: true),
            conversation, autoCompaction: new AutoCompactionPolicy(1_800, 300, 1), keepRecentTokens: 1);
        using var output = new StringWriter();
        await new JsonEventMode(output).RunAsync(run, Continuation);
        var records = ParseLines(output.ToString());
        var start = records.Single(record => record.TryGetProperty("type", out var type) &&
            type.GetString() == "event" && record.TryGetProperty("data", out var data) &&
            data.TryGetProperty("type", out var eventType) && eventType.GetString() == "compaction_start");
        var end = records.Single(record => record.TryGetProperty("type", out var type) &&
            type.GetString() == "event" && record.TryGetProperty("data", out var data) &&
            data.TryGetProperty("type", out var eventType) && eventType.GetString() == "compaction_end");
        var startData = start.GetProperty("data");
        var endData = end.GetProperty("data");
        return new
        {
            startType = startData.GetProperty("type").GetString(),
            startReason = startData.GetProperty("reason").GetString(),
            endType = endData.GetProperty("type").GetString(),
            endReason = endData.GetProperty("reason").GetString(),
            resultSummary = endData.GetProperty("result").GetProperty("summary").GetString(),
            firstKeptEntryPresent = endData.GetProperty("result").TryGetProperty("firstKeptEntryId", out _),
            aborted = endData.GetProperty("aborted").GetBoolean(),
            willRetry = endData.GetProperty("willRetry").GetBoolean(),
            summaryRequestCount = client.SummaryRequestCount
        };
    }

    private static async Task<object> CaptureProviderRequestAsync(string api, IReadOnlyList<ChatMessage> messages,
        string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        var baseUrl = StartListenerAsync(out var listener, out var port);
        var endpoint = new Uri(api == "anthropic-messages" ? baseUrl : baseUrl + "v1/");
        var model = new ModelDescriptor("fixture-model", null, 100_000, null, Provider: "fixture", Api: api,
            MaxOutputTokens: 64, BaseUrl: endpoint.ToString());
        var provider = new ProviderProfile("fixture", "Fixture", endpoint, false, false, null, null, [model], api);
        var selection = new ModelSelection(provider, model, "fixture-key", true, "fixture");
        using var client = ProviderChatClientFactory.Create(selection,
            new ProviderRetrySettings(MaxRetries: 0, TimeoutMs: 10_000));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
            string body;
            using (var reader = new StreamReader(context.Request.InputStream))
                body = await reader.ReadToEndAsync(deadline.Token);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json";
            await using (var writer = new StreamWriter(context.Response.OutputStream))
                await writer.WriteAsync(ProviderResponse(api));
            context.Response.Close();
            return new { path = context.Request.Url!.AbsolutePath, request = JsonDocument.Parse(body).RootElement.Clone() };
        }, deadline.Token);

        try
        {
            _ = await client.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = 64 }, deadline.Token);
            var captured = await server.WaitAsync(deadline.Token);
            var json = captured.request.GetRawText();
            return new
            {
                api,
                path = captured.path,
                containsSummary = json.Contains(Summary, StringComparison.Ordinal),
                containsRetainedHistory = json.Contains("RETAINED ANSWER", StringComparison.Ordinal),
                containsContinuation = json.Contains(Continuation, StringComparison.Ordinal),
                containsOlderRawHistory = json.Contains("OLDER ANSWER", StringComparison.Ordinal)
            };
        }
        finally
        {
            deadline.Cancel();
            listener.Close();
        }
    }

    private static string ProviderResponse(string api, string? text = null) => api switch
    {
        "openai-responses" => """{"id":"resp-c11","object":"response","created_at":1,"status":"completed","model":"fixture-model","output":[{"id":"msg-c11","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"continued"}]}],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2,"input_tokens_details":{"cached_tokens":0},"output_tokens_details":{"reasoning_tokens":0}}}""",
        "openai-completions" => JsonSerializer.Serialize(new
        {
            id = "chat-c11",
            @object = "chat.completion",
            created = 1,
            model = "fixture-model",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = text ?? "continued" }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 24, completion_tokens = 5, total_tokens = 29 }
        }),
        _ => """{"id":"msg-c11","type":"message","role":"assistant","model":"fixture-model","content":[{"type":"text","text":"continued"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}"""
    };

    private static string MessageSnapshot(ChatMessage message) =>
        $"{message.Role}: {string.Join("", message.Contents.OfType<TextContent>().Select(item => item.Text))}";

    private static bool IsBalancedToolHistory(IReadOnlyList<string> messages) =>
        messages.All(message => !message.Contains("tool_call", StringComparison.OrdinalIgnoreCase) &&
                                !message.Contains("tool_result", StringComparison.OrdinalIgnoreCase));

    private static ConversationSession CreateConversation(string workingDirectory, bool includeLargeHistory,
        string? endpoint = null, string? provider = "fixture")
    {
        var conversation = new ConversationSession(workingDirectory, "fixture-model", endpoint, provider);
        var suffix = includeLargeHistory ? new string('x', 1_000) : string.Empty;
        conversation.Append(new ChatMessage(ChatRole.User, "OLDER QUESTION " + suffix));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "OLDER ANSWER " + suffix));
        conversation.Append(new ChatMessage(ChatRole.User, "RETAINED QUESTION"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "RETAINED ANSWER"));
        return conversation;
    }

    private static Task<ConversationRun> OpenRunAsync(ConversationSession conversation, string workingDirectory,
        CaptureClient client, int keepRecentTokens) => ConversationRun.OpenAsync(
        new PiAgent(client, new CodingTools(workingDirectory), noTools: true, retryPolicy: ProviderRetryPolicy.None),
        conversation, keepRecentTokens: keepRecentTokens);

    private static string StartListenerAsync(out HttpListener listener, out int port)
    {
        using var tcp = new TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var root = $"http://127.0.0.1:{port}/";
        listener = new HttpListener();
        listener.Prefixes.Add(root);
        listener.Start();
        return root;
    }

    private static JsonElement[] ParseLines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

    private sealed class CaptureClient(string summary, string response) : IChatClient
    {
        private int _calls;
        private int _summaryRequestCount;
        public string RequestText { get; private set; } = string.Empty;
        public int SummaryRequestCount => Volatile.Read(ref _summaryRequestCount);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capturedMessages = messages.ToArray();
            RequestText = string.Join("\n", capturedMessages.Select(MessageSnapshot));
            if (RequestText.Contains("conversation to summarize", StringComparison.Ordinal) ||
                RequestText.Contains("concise checkpoint", StringComparison.Ordinal))
                Interlocked.Increment(ref _summaryRequestCount);
            var answer = Interlocked.Increment(ref _calls) == 1 ? summary : response;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer))
            {
                Usage = new UsageDetails { InputTokenCount = 24, OutputTokenCount = 5, TotalTokenCount = 29 }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Messages.Single().Contents);
            if (response.Usage is { } usage)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
