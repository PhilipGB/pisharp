using System.Text.Json;
using System.Runtime.CompilerServices;
using PiSharp.Runtime;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Classifiers;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class CodemodeModelTests
{
    [Fact]
    public async Task GuestCatalogAndClassifierAccessUseHostAuthority()
    {
        var models = new Models();
        var registry = new PiSharpToolRegistry([]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction>(), _ => { }, "root", null, "codemode", new Dictionary<string, object?>(), models);
        var result = await CodemodeSandbox.ExecuteAsync("""
            const all = await models.getModelsOfType('classifier', 'fixture');
            const available = await models.getAvailableOfType('classifier');
            const model = await models.getModelOfType('classifier', 'fixture', 'one');
            if(model.headers || all[0].headers || available[0].headers) throw new Error('model headers leaked');
            const absent = await models.getModelOfType('classifier', 'fixture', 'absent');
            text([all.length,available.length,typeof absent]);
            model.baseUrl='https://attacker.invalid';model.headers={Authorization:'guest'};
            const answer=await models.classify(model,{state:{value:7},questions:{q:{type:'bool',instructions:'safe?',criteria:{true:'safe',false:'unsafe'}}}});
            text(answer.answers.q.probability);
            text([answer.usage.input,answer.usage.output,answer.usage.totalTokens,answer.usage.cost.total]);
            """, context, new Dictionary<string, JsonElement>(), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("[1,1,\"undefined\"]\n0.8\n[100,10,110,0.0003]", result.Text);
        Assert.Equal(1, models.Calls);
        Assert.Equal("models.classify", Assert.Single(context.NestedCalls!.Calls).Name);
    }

    [Fact]
    public async Task PaidClassifierUsageIsPublishedEvenWhenTheScriptLaterFails()
    {
        var events = new List<AgentLifecycleEvent>();
        var registry = new PiSharpToolRegistry([]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction>(), events.Add, "root", null, "codemode", new Dictionary<string, object?>(), new Models());
        var result = await CodemodeSandbox.ExecuteAsync("""
            await models.classify({provider:'fixture',id:'one'},{state:{value:7},questions:{q:{type:'bool',instructions:'safe?',criteria:{}}}});
            throw new Error('later failure');
            """, context, new Dictionary<string, JsonElement>(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("later failure", result.Error);
        var usage = Assert.Single(events, item => item.Type == "nested_model_usage").UsageSnapshot;
        Assert.Equal(110, usage!.TotalTokens);
        Assert.Equal(0.0003m, usage.Cost);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuiltinAggregatesClassifierAndNestedToolUsageOnSuccessAndFailure(bool fail)
    {
        var leaf = AIFunctionFactory.Create(() => new PiSharpToolResult("leaf", Usage: new UsageDetails { InputTokenCount = 50, TotalTokenCount = 50 }, Cost: 0.0004m), name: "leaf");
        var registry = new PiSharpToolRegistry([new PiSharpToolRegistration(leaf, ToolExposure.CodeMode)]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction> { ["leaf"] = leaf }, _ => { }, "root", null, "codemode", new Dictionary<string, object?>(), new Models());
        var registration = new ExtensionRegistration();
        CodemodeBuiltin.Configure(registration);
        var code = """
            for(let i=0;i<2;i++) await models.classify({provider:'fixture',id:'one'},{state:{value:7},questions:{q:{type:'bool',instructions:'safe?',criteria:{}}}});
            await tools.leaf({});
            """ + (fail ? "throw new Error('later failure');" : "text('done');");
        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["code"] = code })
        { Context = new Dictionary<object, object?> { [PiSharpToolExecutionContext.ContextKey] = context } };
        var value = await Assert.Single(registration.ToolDefinitions).Function.InvokeAsync(arguments);
        Assert.True(ToolResultOutput.TryReadContract(value, out var result));
        Assert.NotNull(result.Usage);
        Assert.Equal(fail, result.IsError);
        Assert.Equal(270, result.Usage!.TotalTokenCount);
        Assert.Equal(250, result.Usage.InputTokenCount);
        Assert.Equal(0.0010m, result.Cost);
        Assert.Equal(3, context.NestedCalls!.Calls.Count);
        Assert.Equal(new[] { "models.classify", "models.classify", "leaf" }, context.NestedCalls.Calls.Select(call => call.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderLoopPersistsClassifierUsageOnceAndKeepsItOutOfChatContext(bool fail)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codemode-model-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registration = new ExtensionRegistration();
            CodemodeBuiltin.Configure(registration);
            var script = """
                for(let i=0;i<2;i++) await models.classify({provider:'fixture',id:'one'},{state:{value:7},questions:{q:{type:'bool',instructions:'safe?',criteria:{}}}});
                """ + (fail ? "throw new Error('after billing');" : "text('done');");
            using var client = new ScriptClient(script);
            var conversation = new ConversationSession(root, "chat", null);
            var agent = new PiAgent(client, new CodingTools(root), selectedTools: ["codemode"], noBuiltinTools: true,
                extensionToolRegistrations: registration.ToolDefinitions, codemodeModels: new Models());
            var run = await ConversationRun.OpenAsync(agent, conversation);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("run")) events.Add(item);
            Assert.Equal(2, conversation.ActiveUsage().Count);
            Assert.Equal(220, conversation.ActiveUsage().Sum(usage => usage.TotalTokens));
            Assert.Equal(0.0006m, conversation.ActiveUsage().Sum(usage => usage.Cost));
            Assert.All(conversation.ActiveUsage(), usage => Assert.Equal("classifier", usage.Source));
            Assert.Null(conversation.LatestContextUsageTokens());
            var completed = Assert.Single(events, item => item.Type == "tool_execution_finished" && item.Tool == "codemode");
            Assert.Equal(fail, completed.IsError);
            Assert.Equal(0.0006m, completed.Cost);
            Assert.Equal(2, completed.NestedToolCalls!.Calls.Count);
            Assert.Equal(2, ConversationSession.Parse(conversation.ToJson()).ActiveUsage().Count);
            var exported = PiJsonlSessionInterchange.Export(conversation);
            Assert.Contains("0.0006", exported);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ClassifierHostCallsLimitConcurrencyAndCancelQueuedCalls()
    {
        var models = new BlockingModels();
        var registry = new PiSharpToolRegistry([]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction>(), _ => { }, "root", null, "codemode", new Dictionary<string, object?>(), models);
        using var cancellation = new CancellationTokenSource();
        var state = new ClassifierContext(JsonSerializer.SerializeToElement(new { }), new Dictionary<string, ClassifierQuestion>());
        var calls = Enumerable.Range(0, 5).Select(_ => context.ClassifyModelAsync("fixture", "one", state, cancellation.Token)).ToArray();
        await models.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(4, models.Calls);
        Assert.Equal(5, context.NestedCalls!.Calls.Count);
        Assert.False(context.NestedCalls.Complete);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(calls));
        Assert.Equal(4, models.Calls);
        Assert.True(context.NestedCalls.Complete);
        Assert.All(context.NestedCalls.Calls, call => Assert.Equal("cancelled", call.Status));
    }

    [Fact]
    public async Task ProviderErrorReturnsTypedOutcomeAndRetainsBilledUsage()
    {
        var registry = new PiSharpToolRegistry([]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction>(), _ => { }, "root", null, "codemode", new Dictionary<string, object?>(), new Models(error: true));
        var result = await CodemodeSandbox.ExecuteAsync("""
            const r=await models.classify({provider:'fixture',id:'one'},{state:{value:7},questions:{q:{type:'bool',instructions:'safe?',criteria:{}}}});
            text(r.stopReason);
            """, context, new Dictionary<string, JsonElement>(), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("error", result.Text);
        Assert.Equal(0.0003m, Assert.Single(context.NestedUsage).Cost);
        Assert.Equal("error", Assert.Single(context.NestedCalls!.Calls).Status);
    }

    [Fact]
    public async Task UnknownGuestModelIsRejectedBeforeProviderDispatch()
    {
        var models = new Models();
        var registry = new PiSharpToolRegistry([]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction>(), _ => { }, "root", null, "codemode", new Dictionary<string, object?>(), models);
        var result = await CodemodeSandbox.ExecuteAsync("await models.classify({provider:'fixture',id:'absent'},{});", context,
            new Dictionary<string, JsonElement>(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("Unknown classifier", result.Error);
        Assert.Equal(0, models.Calls);
        Assert.Empty(context.NestedUsage);
    }

    private sealed class BlockingModels : ICodemodeModels
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<JsonElement> GetModels(string type, string? provider = null) => [];
        public Task<IReadOnlyList<JsonElement>> GetAvailableAsync(string type, string? provider, CancellationToken token) => Task.FromResult(GetModels(type, provider));
        public async Task<ClassifierResult> ClassifyAsync(string provider, string id, ClassifierContext context, CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) == 4) Started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class ScriptClient(string code) : IChatClient
    {
        private int _requests;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("script", "codemode",
                    new Dictionary<string, object?> { ["code"] = code })]);
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Models(bool error = false) : ICodemodeModels
    {
        public int Calls { get; private set; }
        public IReadOnlyList<JsonElement> GetModels(string type, string? provider = null) =>
            [JsonSerializer.SerializeToElement(new { type = "classifier", provider = "fixture", id = "one", headers = new { Authorization = "host-secret" } })];
        public Task<IReadOnlyList<JsonElement>> GetAvailableAsync(string type, string? provider, CancellationToken cancellationToken) => Task.FromResult(GetModels(type, provider));
        public Task<ClassifierResult> ClassifyAsync(string provider, string id, ClassifierContext context, CancellationToken cancellationToken)
        {
            Assert.Equal("fixture", provider);
            Assert.Equal("one", id);
            Assert.Equal(7, context.State.GetProperty("value").GetInt32());
            Assert.IsType<ClassifierBoolQuestion>(context.Questions["q"]);
            Calls++;
            return Task.FromResult(new ClassifierResult("fixture-api", provider, id,
                error ? new Dictionary<string, ClassifierAnswer>() : new Dictionary<string, ClassifierAnswer> { ["q"] = new ClassifierBoolAnswer(0.8) }, error ? "error" : "stop", new UsageRecord("one", "classifier", 100, 10, 0, 0, 110, 0.0003m), ErrorMessage: error ? "provider failure" : null));
        }
    }
}
