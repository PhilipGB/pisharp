using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BoundaryPiJsonlEntryCodecTests
{
    [Fact]
    public void BoundaryContentEncoderPreservesSupportedPiContentAndRejectsUnsupportedContent()
    {
        var content = PiJsonlEntryCodec.EncodeBoundaryContent([
            new TextContent("before"),
            new TextReasoningContent("thinking"),
            new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
            new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["q"] = "value" })
        ]);

        Assert.Equal(["text", "thinking", "image", "toolCall"], content.EnumerateArray()
            .Select(item => item.GetProperty("type").GetString()));
        Assert.Equal("AQID", content[2].GetProperty("data").GetString());
        Assert.Equal("call-1", content[3].GetProperty("id").GetString());
        Assert.Throws<InvalidDataException>(() => PiJsonlEntryCodec.EncodeBoundaryContent([
            new FunctionResultContent("call-1", "unsupported")
        ]));
    }

    [Fact]
    public void ImportedCustomMessageContextRetainsImagesDetailsAndOriginalPiRecord()
    {
        var session = PiJsonlSessionInterchange.Import(SessionJsonl("""
            {"type":"custom_message","id":"custom-1","parentId":null,"timestamp":"2026-10-01T00:00:00Z","customType":"fixture.image","content":[{"type":"text","text":"caption"},{"type":"image","mimeType":"image/png","data":"AQID"}],"display":false,"details":{"source":"camera","index":2}}
            """));

        var message = Assert.Single(session.ContextMessages());
        Assert.Contains(message.Contents.OfType<DataContent>(), image => image.MediaType == "image/png" &&
            image.Data.Span.SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.True(PiSharpCustomMessageProjection.TryRead(message, out var customMessage));
        Assert.Equal("fixture.image", customMessage.CustomType);
        Assert.Equal("caption", customMessage.Content);
        Assert.Equal("fixture.image", Assert.IsType<string>(message.AdditionalProperties!["pisharp.extension.customType"]));
        Assert.False(Assert.IsType<bool>(message.AdditionalProperties["pisharp.extension.display"]));
        Assert.Equal("AQID", Assert.IsType<JsonElement>(message.AdditionalProperties["pisharp.extension.piContent"])
            .EnumerateArray().Last().GetProperty("data").GetString());
        Assert.Equal(2, Assert.IsType<JsonElement>(message.AdditionalProperties["pisharp.extension.details"])
            .GetProperty("index").GetInt32());
        var runtimeProjection = PiJsonlEntryCodec.ProjectRuntimeMessage(session, message);
        using var runtimeProjectionDocument = JsonDocument.Parse(runtimeProjection.ToJsonString());
        Assert.Equal("AQID", runtimeProjectionDocument.RootElement.GetProperty("content")[1]
            .GetProperty("data").GetString());
        Assert.Equal(2, runtimeProjectionDocument.RootElement.GetProperty("details").GetProperty("index").GetInt32());

        var exported = PiJsonlSessionInterchange.Export(session);
        using var exportedEntry = JsonDocument.Parse(exported.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
        Assert.Equal("image/png", exportedEntry.RootElement.GetProperty("content")[1].GetProperty("mimeType").GetString());
        Assert.Equal(2, exportedEntry.RootElement.GetProperty("details").GetProperty("index").GetInt32());
        var resumed = PiJsonlSessionInterchange.Import(exported);
        Assert.Contains(Assert.Single(resumed.ContextMessages()).Contents.OfType<DataContent>(), image =>
            image.Data.Span.SequenceEqual(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void ContextEditsRetainAssistantMetadataAndReplaceToolCalls()
    {
        var session = PiJsonlSessionInterchange.Import(SessionJsonl("""
            {"type":"message","id":"assistant-1","parentId":null,"timestamp":"2026-10-01T00:00:00Z","message":{"role":"assistant","content":[{"type":"text","text":"old"}],"provider":"fixture","model":"model-a","api":"fixture-api","thinkingLevel":"high","stopReason":"stop"}}
            {"type":"context_edit","id":"edit-assistant","parentId":"assistant-1","timestamp":"2026-10-01T00:00:01Z","targetId":"assistant-1","replacement":{"content":[{"type":"thinking","thinking":"replacement reasoning"},{"type":"toolCall","id":"call-new","name":"lookup","arguments":{"q":"new"}}]}}
            """));

        var assistant = Assert.Single(session.ContextMessages());
        var properties = Assert.IsAssignableFrom<IDictionary<string, object?>>(assistant.AdditionalProperties);
        Assert.Equal("fixture", properties["pisharp.provider"]);
        Assert.Equal("model-a", properties["pisharp.model"]);
        Assert.Equal("fixture-api", properties["pisharp.api"]);
        Assert.Equal("high", properties["pisharp.thinkingLevel"]);
        Assert.Equal("replacement reasoning", Assert.Single(assistant.Contents.OfType<TextReasoningContent>()).Text);
        var call = Assert.Single(assistant.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call-new", call.CallId);
        Assert.Equal("new", Assert.IsType<string>(call.Arguments!["q"]));
    }

    [Fact]
    public void ToolResultContextEditRetainsCallIdentityAndErrorContractAcrossJsonlRoundTrip()
    {
        var session = PiJsonlSessionInterchange.Import(SessionJsonl("""
            {"type":"message","id":"tool-1","parentId":null,"timestamp":"2026-10-01T00:00:00Z","message":{"role":"toolResult","toolCallId":"call-1","content":[{"type":"text","text":"old"}],"isError":true,"piSharpToolResult":{"piSharpToolResult":true,"text":"old","details":{"code":7},"isError":true,"error":"original failure"}}}
            {"type":"context_edit","id":"edit-tool","parentId":"tool-1","timestamp":"2026-10-01T00:00:01Z","targetId":"tool-1","replacement":{"content":[{"type":"text","text":"replacement"}]}}
            """));

        var result = Assert.Single(Assert.Single(session.ContextMessages()).Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-1", result.CallId);
        Assert.True(ToolResultOutput.TryReadContract(result.Result, out var contract));
        Assert.Equal("replacement", contract.Text);
        Assert.True(contract.IsError);
        Assert.Equal("original failure", contract.Error);
        Assert.Equal(7, Assert.IsType<JsonElement>(contract.Details).GetProperty("code").GetInt32());

        var roundTripped = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(session));
        var persistedResult = Assert.Single(Assert.Single(roundTripped.ContextMessages()).Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-1", persistedResult.CallId);
        Assert.True(ToolResultOutput.TryReadContract(persistedResult.Result, out var persistedContract));
        Assert.Equal("replacement", persistedContract.Text);
        Assert.True(persistedContract.IsError);
        Assert.Equal("original failure", persistedContract.Error);
    }

    [Fact]
    public void NativeToolResultContextEditRetainsResultWrapperAuthorAndErrorMetadata()
    {
        var session = new ConversationSession(Path.GetTempPath(), "native-tool-edit", null);
        session.Append(new ChatMessage(ChatRole.User, "request"));
        session.Append(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("native-call", "probe", new Dictionary<string, object?>())]));
        var result = new FunctionResultContent("native-call",
            new PiSharpToolResult("old output", Details: new { code = 11 }, IsError: true, Error: "tool failed"))
        {
            Exception = new InvalidOperationException("tool failed")
        };
        ToolExecutionDuration.Set(result, 12.5);
        session.Append(new ChatMessage(ChatRole.Tool, [result]) { AuthorName = "probe" });
        var targetId = session.Tree.HeadId!;
        session.Tree.Append("context_edit", JsonSerializer.SerializeToElement(new
        {
            targetId,
            replacement = new
            {
                content = new[] { new { type = "text", text = "edited output" } }
            }
        }));

        var edited = session.ContextMessages()[^1];
        var editedResult = Assert.Single(edited.Contents.OfType<FunctionResultContent>());
        Assert.Equal("probe", edited.AuthorName);
        Assert.Equal("native-call", editedResult.CallId);
        Assert.NotNull(editedResult.Exception);
        Assert.Equal(12.5, ToolExecutionDuration.Get(editedResult));
        Assert.True(ToolResultOutput.TryReadContract(editedResult.Result, out var contract));
        Assert.Equal("edited output", contract.Text);
        Assert.True(contract.IsError);
        Assert.Equal("tool failed", contract.Error);
        Assert.Equal(11, Assert.IsType<JsonElement>(contract.Details).GetProperty("code").GetInt32());
    }

    [Fact]
    public void NativeSelfRetainingCompactionExportsOwnBoundaryAndUsage()
    {
        var session = new ConversationSession(Path.GetTempPath(), "compaction-export", null);
        using var payloadDocument = JsonDocument.Parse("""
            {"summary":"checkpoint","firstKeptEntryId":null,"tokensBefore":99,"fromHook":true,"usage":{"input":10,"output":4,"cacheRead":2,"cacheWrite":1,"totalTokens":17}}
            """);
        var node = new PiSharp.Core.ConversationNode("compact-1", null, "compaction",
            payloadDocument.RootElement.Clone(), DateTimeOffset.UnixEpoch);

        var projected = PiJsonlEntryCodec.ExportNativeEntry(session, node);

        Assert.Equal("compact-1", projected["firstKeptEntryId"]!.GetValue<string>());
        Assert.Equal(10, projected["usage"]!["input"]!.GetValue<int>());
        Assert.Equal(17, projected["usage"]!["totalTokens"]!.GetValue<int>());
        Assert.True(projected["fromHook"]!.GetValue<bool>());
    }

    private static string SessionJsonl(string entries) => $$"""
        {"type":"session","version":3,"id":"codec-fixture","timestamp":"2026-10-01T00:00:00Z","cwd":"{{Path.GetFullPath(Path.GetTempPath())}}"}
        {{entries}}
        """;
}
