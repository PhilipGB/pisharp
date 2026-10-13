using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ToolSearchTests
{
    [Fact]
    public async Task SearchLoadsDeferredToolIntoNextRequestAndPersistsBranchLoadout()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-tool-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var registration = new ExtensionRegistration();
            registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(ResolveTicket, name: "resolve_ticket"),
                ToolExposure.Deferred, Namespace: new("tracker", "Project tickets")));
            registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(ReadWeather, name: "read_weather"),
                ToolExposure.CodeMode, Namespace: new("weather", "Forecast tools")));
            ToolSearchBuiltin.Configure(registration);
            var client = new SearchClient();
            var conversation = new ConversationSession(cwd, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd), noBuiltinTools: true,
                extensionToolRegistrations: registration.ToolDefinitions), conversation);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("find the ticket")) events.Add(item);

            Assert.Equal(["tool_search"], client.RequestTools[0]);
            Assert.Equal(["resolve_ticket", "tool_search"], client.RequestTools[1].Order(StringComparer.Ordinal));
            Assert.DoesNotContain("read_weather", client.RequestTools[1]);
            Assert.Equal("ticket:42", client.FinalToolResult);
            var searchEnd = Assert.Single(events, item => item.Type == "tool_execution_finished" && item.Tool == "tool_search");
            Assert.Contains("resolve_ticket", searchEnd.Text);
            using var details = JsonDocument.Parse(JsonSerializer.Serialize(searchEnd.Details));
            Assert.Equal("resolve_ticket", details.RootElement.GetProperty("loaded")[0].GetString());
            Assert.Contains("resolve_ticket", conversation.ActiveToolLoadout()!);

            var change = Assert.Single(conversation.Tree.Entries, entry => entry.Type == "tool_loadout");
            var loadedHead = conversation.Tree.HeadId;
            conversation.Tree.Select(change.ParentId);
            Assert.Null(conversation.ActiveToolLoadout());
            conversation.Tree.Select(loadedHead);

            var imported = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(conversation));
            Assert.Contains("resolve_ticket", imported.ActiveToolLoadout()!);
            var resumedClient = new SearchClient();
            var resumed = await ConversationRun.OpenAsync(new PiAgent(resumedClient, new CodingTools(cwd),
                noBuiltinTools: true, extensionToolRegistrations: registration.ToolDefinitions), imported);
            await foreach (var _ in resumed.RunEventsAsync("resume")) { }
            Assert.Contains("resolve_ticket", resumedClient.RequestTools[0]);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public void RankUsesNamesParametersAndNamespacesWithStableTies()
    {
        var schemaTool = new PiSharpToolRegistration(AIFunctionFactory.Create(ResolveTicket, name: "resolve_ticket"),
            ToolExposure.Deferred, Namespace: new("tracker", "Project tickets"));
        var weatherTool = new PiSharpToolRegistration(AIFunctionFactory.Create(ReadWeather, name: "read_weather"),
            ToolExposure.Deferred, Namespace: new("forecast", "Climate reports"));
        Assert.Equal("resolve_ticket", Assert.Single(ToolSearchRanker.Rank("ticket", [schemaTool, weatherTool], 1)).Function.Name);
        Assert.Equal("resolve_ticket", Assert.Single(ToolSearchRanker.Rank("issue number", [schemaTool, weatherTool], 1)).Function.Name);
        Assert.Equal("read_weather", Assert.Single(ToolSearchRanker.Rank("climate", [schemaTool, weatherTool], 1)).Function.Name);
        Assert.Equal(["read_weather", "resolve_ticket"], ToolSearchRanker.Rank("read ticket weather", [schemaTool, weatherTool], 2)
            .Select(tool => tool.Function.Name).Order(StringComparer.Ordinal));
    }

    [Description("Resolve a project issue by its ticket number.")]
    private static string ResolveTicket([Description("The ticket or issue number.")] string ticket) => "ticket:" + ticket;

    [Description("Read the weather forecast for a location.")]
    private static string ReadWeather([Description("City name.")] string city) => "sunny:" + city;

    private sealed class SearchClient : IChatClient
    {
        private int _requests;
        public List<string[]> RequestTools { get; } = [];
        public string? FinalToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestTools.Add(options?.Tools?.OfType<AIFunction>().Select(tool => tool.Name).ToArray() ?? []);
            switch (Interlocked.Increment(ref _requests))
            {
                case 1:
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        [new FunctionCallContent("search-call", "tool_search", new Dictionary<string, object?>
                        {
                            ["query"] = "ticket issue number"
                        })]);
                    break;
                case 2:
                    yield return new ChatResponseUpdate(ChatRole.Assistant,
                        [new FunctionCallContent("ticket-call", "resolve_ticket", new Dictionary<string, object?>
                        {
                            ["ticket"] = "42"
                        })]);
                    break;
                default:
                    FinalToolResult = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                        .Single(result => result.CallId == "ticket-call").Result?.ToString();
                    yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
                    break;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
