using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class SessionShareExportTests
{
    private const string Source = """
        {"type":"session","version":3,"id":"share-session","timestamp":"2026-10-13T12:00:00.000Z","cwd":"/tmp","extra":"header metadata"}
        {"type":"custom","customType":"fixture","id":"root","parentId":null,"timestamp":"2026-10-13T12:00:01.000Z","data":{"retained":true}}
        {"type":"message","id":"selected","parentId":"root","timestamp":"2026-10-13T12:00:02.000Z","message":{"role":"user","content":"selected prompt","timestamp":1}}
        {"type":"message","id":"inactive","parentId":"root","timestamp":"2026-10-13T12:00:03.000Z","message":{"role":"user","content":"other prompt","timestamp":2}}
        """;
    private static JsonElement Presentation => JsonSerializer.SerializeToElement(new
    {
        systemPrompt = "share </script> 💡",
        tools = new[] { new { name = "fixture", description = "description",
            parameters = new { type = "object", properties = new { value = new { type = "string" } } } } }
    });

    [Fact]
    public void BranchShareContainsOnlyTheSelectedPathAndExportOnlyPresentation()
    {
        var session = PiJsonlSessionInterchange.Import(Source);
        session.Tree.Select("selected");
        var before = session.ToJson();
        var text = SessionShareExport.ExportBranch(session, Presentation);
        Assert.EndsWith("\n", text);
        var records = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.Equal(4, records.Length);
        Assert.Equal("session", records[0].GetProperty("type").GetString());
        Assert.Equal("share-session", records[0].GetProperty("id").GetString());
        Assert.False(records[0].TryGetProperty("extra", out _));
        Assert.Equal("root", records[1].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, records[1].GetProperty("parentId").ValueKind);
        Assert.Equal("selected", records[2].GetProperty("id").GetString());
        Assert.Equal("root", records[2].GetProperty("parentId").GetString());
        Assert.Equal("pi.share", records[3].GetProperty("customType").GetString());
        Assert.Equal("selected", records[3].GetProperty("parentId").GetString());
        Assert.Equal(8, records[3].GetProperty("id").GetString()!.Length);
        Assert.True(JsonElement.DeepEquals(Presentation, records[3].GetProperty("data")));
        Assert.Equal(records[0].GetProperty("timestamp").GetString(), records[3].GetProperty("timestamp").GetString());
        Assert.Equal(before, session.ToJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyShareLinksPresentationToANullParent(bool retainedHistory)
    {
        var session = retainedHistory ? PiJsonlSessionInterchange.Import(Source) : new ConversationSession(Path.GetTempPath(), "fixture", null);
        session.Tree.Select(null);
        var records = SessionShareExport.ExportBranch(session, Presentation).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, records.Length);
        using var trailing = JsonDocument.Parse(records[1]);
        Assert.Equal(JsonValueKind.Null, trailing.RootElement.GetProperty("parentId").ValueKind);
    }

    [Fact]
    public async Task GistHtmlEmbedsCompatibleViewerDataWithAllBranchesAndSafePresentation()
    {
        var session = PiJsonlSessionInterchange.Import(Source);
        session.Tree.Select("selected");
        var path = Path.Combine(Path.GetTempPath(), "pisharp-share-html-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            await SessionExport.ExportHtmlAsync(session, path, sharePresentation: Presentation);
            var html = await File.ReadAllTextAsync(path);
            const string prefix = "<script id=\"session-data\" type=\"application/json\">";
            var start = html.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
            var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(html[start..end])));
            Assert.Equal("header metadata", json.RootElement.GetProperty("header").GetProperty("extra").GetString());
            Assert.Equal("selected", json.RootElement.GetProperty("leafId").GetString());
            Assert.Equal(3, json.RootElement.GetProperty("entries").GetArrayLength());
            Assert.Equal("share </script> 💡", json.RootElement.GetProperty("systemPrompt").GetString());
            Assert.True(JsonElement.DeepEquals(Presentation.GetProperty("tools"), json.RootElement.GetProperty("tools")));
            Assert.DoesNotContain("share </script>", html);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PresentationUsesTheCurrentLoadoutAfterLiveExtensionRegistration()
    {
        var registration = new ExtensionRegistration();
        registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create((string value) => value,
            name: "initial", description: "initial description")));
        using var client = new UnusedClient();
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), noBuiltinTools: true,
            systemPrompt: "fixture system", extensionToolRegistrations: registration.ToolDefinitions,
            liveExtensionRegistration: registration);
        var run = await ConversationRun.OpenAsync(agent, new ConversationSession(Path.GetTempPath(), "fixture", null));
        Assert.Single(run.SharePresentation.GetProperty("tools").EnumerateArray());
        registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(() => "late", name: "late", description: "late description")));
        var presentation = run.SharePresentation;
        Assert.Equal(agent.SystemInstructions, presentation.GetProperty("systemPrompt").GetString());
        Assert.StartsWith("fixture system\n\n<cwd>", presentation.GetProperty("systemPrompt").GetString());
        var tools = presentation.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(["initial", "late"], tools.Select(tool => tool.GetProperty("name").GetString()));
        Assert.Equal("initial description", tools[0].GetProperty("description").GetString());
        Assert.Equal("string", tools[0].GetProperty("parameters").GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
    }

    private sealed class UnusedClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
