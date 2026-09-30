using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class CodemodeTests
{
    [Fact]
    public void CodemodeRegistersInactiveByDefault()
    {
        var registration = new ExtensionRegistration();
        CodemodeBuiltin.Configure(registration);
        var registry = new PiSharpToolRegistry(registration.ToolDefinitions);
        Assert.Empty(registry.CreateLoadout().Snapshot.Declared);
        Assert.Equal("codemode", Assert.Single(registry.CreateLoadout(["codemode"]).Snapshot.Declared)
            .Registration.Function.Name);
        registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(Echo, name: "echo"),
            ToolExposure.CodeMode));
        registry = new PiSharpToolRegistry(registration.ToolDefinitions);
        var description = Assert.Single(registry.CreateLoadout(["codemode"]).Snapshot.Declared).Description;
        Assert.Contains("declare const tools", description);
        Assert.Contains("echo(args:", description);
    }

    [Fact]
    public async Task BuiltinRunsNestedToolAndPersistsStoreAcrossResume()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-codemode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var registration = new ExtensionRegistration();
            registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(Echo, name: "echo"),
                ToolExposure.CodeMode));
            CodemodeBuiltin.Configure(registration);
            var client = new ScriptClient("const matches=await searchTools('return value',{limit:1}); " +
                "const info=await describeTool('echo'); " +
                "text(matches[0].name+':'+(info.includes('declare const tools')?'decl':'missing')); " +
                "text(typeof tools.codemode); store('answer', 42); text(await tools.echo({value:'nested'})); return load('answer')");
            var conversation = new ConversationSession(cwd, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd),
                selectedTools: ["codemode"], noBuiltinTools: true,
                extensionToolRegistrations: registration.ToolDefinitions), conversation);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("run script")) events.Add(item);
            Assert.Equal(["codemode"], client.RequestTools[0]);
            Assert.Equal("echo:decl\nundefined\nnested\n42", client.Result);
            Assert.Contains(events, item => item.Type == "tool_execution_finished" && item.Tool == "echo" &&
                item.ParentToolCallId == "script-call");
            Assert.Equal(42, conversation.ActiveCodemodeStore()!["answer"].GetInt32());
            var storeEntry = Assert.Single(conversation.Tree.Entries, entry => entry.Type == "codemode_store");
            var savedHead = conversation.Tree.HeadId;
            conversation.Tree.Select(storeEntry.ParentId);
            Assert.Null(conversation.ActiveCodemodeStore());
            conversation.Tree.Select(savedHead);

            var imported = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(conversation));
            Assert.Equal(42, imported.ActiveCodemodeStore()!["answer"].GetInt32());
            var resumedClient = new ScriptClient("text(load('answer'))");
            var resumed = await ConversationRun.OpenAsync(new PiAgent(resumedClient, new CodingTools(cwd),
                selectedTools: ["codemode"],
                noBuiltinTools: true, extensionToolRegistrations: registration.ToolDefinitions), imported);
            await foreach (var _ in resumed.RunEventsAsync("resume")) { }
            Assert.Equal("42", resumedClient.Result);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task RejectsInvalidImageBeforeItIsPersistedOrReplayedToProvider()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-codemode-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var registration = new ExtensionRegistration();
            CodemodeBuiltin.Configure(registration);
            var client = new ScriptClient("image('data:image/png;base64,AAAA')");
            var conversation = new ConversationSession(cwd, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd),
                selectedTools: ["codemode"], noBuiltinTools: true,
                extensionToolRegistrations: registration.ToolDefinitions), conversation);

            await foreach (var _ in run.RunEventsAsync("try invalid image")) { }

            Assert.Equal(2, client.RequestMessages.Count);
            Assert.DoesNotContain(client.RequestMessages[1].SelectMany(message => message.Contents),
                content => content is DataContent);
            Assert.DoesNotContain(conversation.ActiveMessages().SelectMany(message => message.Contents),
                content => content is DataContent);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task CodemodeImagesUseDetectedTypeAndNormalizeWrappedBase64()
    {
        var registry = new PiSharpToolRegistry([]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction>(), _ => { }, "test", null, "codemode",
            new Dictionary<string, object?>());
        var images = new (string DataUrl, string MimeType, string Base64)[]
        {
            ("data:image/png;base64,iVBORw0KGgo=", "image/png", "iVBORw0KGgo="),
            ("data:image/jpeg;base64,/9j/4A==", "image/jpeg", "/9j/4A=="),
            ("data:image/gif;base64,R0lGODlh", "image/gif", "R0lGODlh"),
            ("data:image/webp;base64,UklGRgAAAABXRUJQ", "image/webp", "UklGRgAAAABXRUJQ"),
            ("data:image/jpeg;base64,iVBORw0KGgo=", "image/png", "iVBORw0KGgo="),
            ("data:image/png;base64,iVBORw0K\r\nGgo=\n", "image/png", "iVBORw0KGgo=")
        };

        foreach (var (dataUrl, mimeType, base64) in images)
        {
            var result = await CodemodeSandbox.ExecuteAsync("image(" + JsonSerializer.Serialize(dataUrl) + ")",
                context, new Dictionary<string, JsonElement>(), CancellationToken.None);
            Assert.True(result.Ok, result.Error);
            var image = Assert.Single(result.Images!);
            Assert.Equal(mimeType, image.MimeType);
            Assert.Equal(base64, image.DataBase64);
        }
    }

    [Fact]
    public async Task CodemodeRejectsMalformedAndUnsupportedImageData()
    {
        var registry = new PiSharpToolRegistry([]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction>(), _ => { }, "test", null, "codemode",
            new Dictionary<string, object?>());
        var dataUrls = new[]
        {
            "data:image/png;base64,AAAA!",
            "data:image/png;base64,AAAAA",
            "data:image/png;base64,AA=A",
            "data:image/png;base64,AAAA",
            "data:image/png;base64,QUJD",
            "data:image/jpeg;base64,/9j/9w=="
        };

        foreach (var dataUrl in dataUrls)
        {
            var result = await CodemodeSandbox.ExecuteAsync("image(" + JsonSerializer.Serialize(dataUrl) + ")",
                context, new Dictionary<string, JsonElement>(), CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Empty(result.Images ?? []);
        }
    }

    [Fact]
    public async Task SandboxHasNoHostProcessAndStopsInfiniteLoopOnCancellation()
    {
        var registry = new PiSharpToolRegistry([]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction>(), _ => { }, "test", null, "codemode",
            new Dictionary<string, object?>());
        var denied = await CodemodeSandbox.ExecuteAsync("text(typeof process); text(typeof require); text(typeof fetch)",
            context, new Dictionary<string, System.Text.Json.JsonElement>(), CancellationToken.None);
        Assert.True(denied.Ok, denied.Error);
        Assert.Equal("undefined\nundefined\nundefined", denied.Text);
        var returned = await CodemodeSandbox.ExecuteAsync("text('start'); return {answer:7}", context,
            new Dictionary<string, System.Text.Json.JsonElement>(), CancellationToken.None);
        Assert.True(returned.Ok, returned.Error);
        Assert.Equal("start\n{\"answer\":7}", returned.Text);
        var early = await CodemodeSandbox.ExecuteAsync("console.log('early'); exit(); text('late')", context,
            new Dictionary<string, System.Text.Json.JsonElement>(), CancellationToken.None);
        Assert.True(early.Ok, early.Error);
        Assert.Equal("early", early.Text);
        var image = await CodemodeSandbox.ExecuteAsync(
            "image('data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Yk0U2sAAAAASUVORK5CYII=')",
            context, new Dictionary<string, System.Text.Json.JsonElement>(), CancellationToken.None);
        Assert.True(image.Ok, image.Error);
        Assert.Equal("image/png", Assert.Single(image.Images!).MimeType);
        var failed = await CodemodeSandbox.ExecuteAsync("store('x', 1); throw new Error('boom')", context,
            new Dictionary<string, System.Text.Json.JsonElement>(), CancellationToken.None);
        Assert.False(failed.Ok);
        Assert.Null(failed.Store);
        Assert.Contains("boom", failed.Error);
        var partial = await CodemodeSandbox.ExecuteAsync("text('before'); throw new Error('boom')", context,
            new Dictionary<string, System.Text.Json.JsonElement>(), CancellationToken.None);
        Assert.False(partial.Ok);
        Assert.Equal("before", partial.Text);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var stopped = await CodemodeSandbox.ExecuteAsync("while(true) {}", context,
            new Dictionary<string, System.Text.Json.JsonElement>(), timeout.Token);
        Assert.False(stopped.Ok);
        Assert.Equal("Codemode was cancelled.", stopped.Error);
    }

    [Fact]
    public async Task CancellationStopsOutstandingNestedCall()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var function = AIFunctionFactory.Create(async (CancellationToken token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return "unreachable";
        }, name: "block");
        var registry = new PiSharpToolRegistry([new PiSharpToolRegistration(function, ToolExposure.CodeMode)]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction> { ["block"] = function }, _ => { }, "test", null,
            "codemode", new Dictionary<string, object?>());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = CodemodeSandbox.ExecuteAsync("await tools.block({})", context,
            new Dictionary<string, System.Text.Json.JsonElement>(), stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        var result = await run;
        Assert.False(result.Ok);
        Assert.Equal("Codemode was cancelled.", result.Error);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ScriptReceivesStructuredNestedResult()
    {
        var function = AIFunctionFactory.Create(() => new PiSharpToolResult("reported",
            StructuredContent: System.Text.Json.JsonSerializer.SerializeToElement(new { answer = 7 })), name: "report");
        var outputSchema = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { answer = new { type = "integer" } },
            required = new[] { "answer" }
        });
        var registry = new PiSharpToolRegistry([new PiSharpToolRegistration(function, ToolExposure.CodeMode,
            OutputSchema: outputSchema)]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction> { ["report"] = function }, _ => { }, "test", null,
            "codemode", new Dictionary<string, object?>());
        var result = await CodemodeSandbox.ExecuteAsync("text((await tools.report({})).answer)", context,
            new Dictionary<string, System.Text.Json.JsonElement>(), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("7", result.Text);
    }

    [Fact]
    public async Task FailedNestedCallRejectsInsideScript()
    {
        var function = AIFunctionFactory.Create((Func<string>)(() =>
            throw new InvalidOperationException("blocked")), name: "fail");
        var registry = new PiSharpToolRegistry([new PiSharpToolRegistration(function, ToolExposure.CodeMode)]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction> { ["fail"] = function }, _ => { }, "test", null,
            "codemode", new Dictionary<string, object?>());
        var result = await CodemodeSandbox.ExecuteAsync(
            "try { await tools.fail({}) } catch(error) { text(error.message) }", context,
            new Dictionary<string, System.Text.Json.JsonElement>(), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("blocked", result.Text);
    }

    [Fact]
    public async Task DiscoveryBridgeCannotShadowAnAllowedTool()
    {
        var function = AIFunctionFactory.Create(() => "ordinary tool", name: "__search_tools");
        var registry = new PiSharpToolRegistry([new PiSharpToolRegistration(function, ToolExposure.CodeMode)]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(),
            () => new Dictionary<string, AIFunction> { ["__search_tools"] = function }, _ => { }, "test", null,
            "codemode", new Dictionary<string, object?>());
        var result = await CodemodeSandbox.ExecuteAsync("text(await tools['__search_tools']({}))", context,
            new Dictionary<string, System.Text.Json.JsonElement>(), CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("ordinary tool", result.Text);
    }

    [Description("Return the given value.")]
    private static string Echo(string value) => value;

    private sealed class ScriptClient(string code) : IChatClient
    {
        private int _requests;
        public List<string[]> RequestTools { get; } = [];
        public List<ChatMessage[]> RequestMessages { get; } = [];
        public string? Result { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            RequestMessages.Add(snapshot);
            RequestTools.Add(options?.Tools?.OfType<AIFunction>().Select(tool => tool.Name).ToArray() ?? []);
            if (Interlocked.Increment(ref _requests) == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("script-call", "codemode", new Dictionary<string, object?>
                    {
                        ["code"] = code
                    })]);
            else
            {
                Result = snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                    .Last(result => result.CallId == "script-call").Result?.ToString();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
