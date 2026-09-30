using Microsoft.Extensions.AI;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Tests;

public sealed class CodemodeSourceTests
{
    [Theory]
    [InlineData("// @options: {\"unknown\":1}\ntext('executed')")]
    [InlineData("// @options: []\ntext('executed')")]
    [InlineData("// @options: {\"max_output_tokens\":9007199254740992}\ntext('executed')")]
    [InlineData("// @options: {\"timeout_ms\":2147483648}\ntext('executed')")]
    [InlineData("// @options: {\"timeout_ms\":0}\ntext('executed')")]
    [InlineData("// @options: {\"max_output_tokens\":-1}\ntext('executed')")]
    [InlineData("// @options: {\"timeout_ms\":1.5}\ntext('executed')")]
    [InlineData("// @options: {}")]
    public async Task InvalidDirectiveDoesNotExecute(string source)
    {
        var result = await CodemodeSandbox.ExecuteAsync(source, Context(), new Dictionary<string, System.Text.Json.JsonElement>(), default);
        Assert.False(result.Ok);
        Assert.Empty(result.Text);
        Assert.Contains("@options", result.Error);
    }

    [Theory]
    [InlineData("// @options: {}\ntext('ok')", 10000, 30000)]
    [InlineData(" \t// @options: {\"max_output_tokens\":0,\"timeout_ms\":1}\r\ntext('ok')", 0, 1)]
    [InlineData("// @options: {\"timeout_ms\":2147483647}\ntext('ok')", 10000, 30000)]
    public void ValidDirectivePreservesSourceLinesAndSafetyCeiling(string source, long tokens, int timeout)
    {
        var parsed = CodemodeSource.Parse(source);
        Assert.StartsWith("\n", parsed.Code);
        Assert.Equal(tokens, parsed.MaxOutputTokens);
        Assert.Equal(timeout, parsed.TimeoutMilliseconds);
    }

    [Fact]
    public async Task OutputBudgetKeepsHeadTailAndSpillsFullOutput()
    {
        var registry = new ExtensionRegistration();
        CodemodeBuiltin.Configure(registry);
        var loadout = new PiSharpToolRegistry(registry.ToolDefinitions).CreateLoadout(["codemode"]);
        var context = PiSharpToolExecutionContext.CreateRoot(loadout, () => registry.Tools.ToDictionary(tool => tool.Name),
            _ => { }, "root", "operation", "codemode", new Dictionary<string, object?>());
        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["code"] = "// @options: {\"max_output_tokens\":4}\ntext('abcdefghij'.repeat(10));store('kept',true)"
        })
        { Context = new Dictionary<object, object?> { [PiSharpToolExecutionContext.ContextKey] = context } };
        var value = await Assert.Single(registry.Tools).InvokeAsync(arguments);
        Assert.True(PiSharp.Runtime.Tools.ToolResultOutput.TryReadContract(value, out var result));
        Assert.False(result.IsError);
        Assert.StartsWith("Warning: truncated output", result.Text);
        Assert.Contains("abcdefgh…21 tokens truncated…cdefghij", result.Text);
        var details = System.Text.Json.JsonSerializer.SerializeToElement(result.Details);
        var path = details.GetProperty("fullOutputPath").GetString()!;
        try { Assert.Equal(string.Concat(Enumerable.Repeat("abcdefghij", 10)), await File.ReadAllTextAsync(path)); }
        finally { File.Delete(path); }
        Assert.True(context.CodemodeStore["kept"].GetBoolean());
    }

    [Fact]
    public async Task DirectiveTimeoutStopsOutstandingHostCall()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = AIFunctionFactory.Create(async (CancellationToken token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.TrySetResult(); }
            return "never";
        }, name: "wait");
        var registry = new PiSharpToolRegistry([new(tool)]);
        var context = PiSharpToolExecutionContext.CreateRoot(registry.CreateLoadout(["wait"]),
            () => new Dictionary<string, AIFunction> { ["wait"] = tool }, _ => { }, "root", "op", "codemode", new Dictionary<string, object?>());
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        var running = CodemodeSandbox.ExecuteAsync("// @options: {\"timeout_ms\":1000}\nawait tools.wait({})", context,
            new Dictionary<string, System.Text.Json.JsonElement>(), cleanup.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Ok);
        Assert.Contains("1000", result.Error);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static PiSharpToolExecutionContext Context() => PiSharpToolExecutionContext.CreateRoot(
        new PiSharpToolRegistry([]).CreateLoadout([]), () => new Dictionary<string, AIFunction>(), _ => { },
        "root", "op", "codemode", new Dictionary<string, object?>());
}
