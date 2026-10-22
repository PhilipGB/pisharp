using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BuiltinToolArgumentParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pisharp-builtin-args-" + Guid.NewGuid().ToString("N"));

    public BuiltinToolArgumentParityTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "file.txt"), "alpha\nbeta\n");
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("bash")]
    [InlineData("grep")]
    [InlineData("find")]
    [InlineData("ls")]
    public void EveryDeclarationMatchesTheSourceExtractedPinnedSchema(string name)
    {
        using var inventory = Fixture("builtin-coding-tool-inventory-2026-10-22.json");
        var expected = inventory.RootElement.GetProperty("tools").GetProperty(name).GetProperty("declaration");
        var function = Function(name);

        Assert.Equal(expected.GetProperty("description").GetString(), function.Description);
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("parameters"), function.JsonSchema), function.JsonSchema.ToString());
    }

    public static IEnumerable<object[]> ValidationCases()
    {
        using var fixture = Fixture("builtin-coding-tool-validation-2026-10-22.json");
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("caseId").GetString()!, item.GetProperty("tool").GetString()!,
                item.GetProperty("arguments").GetRawText(), item.GetProperty("error").GetString()!];
    }

    public static IEnumerable<object[]> ScalarCases()
    {
        using var fixture = Fixture("builtin-coding-tool-scalar-validation-2026-10-22.json");
        foreach (var item in fixture.RootElement.GetProperty("results").EnumerateArray())
            yield return [item.GetProperty("caseId").GetString()!, item.GetProperty("tool").GetString()!,
                item.GetProperty("input").GetRawText(), item.GetProperty("validated").GetRawText()];
    }

    [Theory]
    [MemberData(nameof(ScalarCases))]
    public void PrimitiveConversionsMatchThePinnedValidator(string caseId, string tool, string input, string expected)
    {
        using var document = JsonDocument.Parse(input);
        using var expectedDocument = JsonDocument.Parse(expected);
        var arguments = new AIFunctionArguments(document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));

        BuiltinToolArguments.PrepareAndValidate(tool, Function(tool).JsonSchema, arguments);

        Assert.True(JsonElement.DeepEquals(expectedDocument.RootElement, JsonSerializer.SerializeToElement(arguments)), caseId);
    }

    [Theory]
    [InlineData("{\"path\":12,\"content\":42}", "12", "42")]
    [InlineData("{\"path\":null,\"content\":false}", "null", "false")]
    public async Task ConvertedWriteArgumentsUseTheExistingWorkspaceAndMutationPath(string input, string path, string content)
    {
        using var document = JsonDocument.Parse(input);
        var arguments = new AIFunctionArguments(document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));

        var result = await Function("write").InvokeAsync(arguments);

        Assert.Equal($"Successfully wrote to {path}", result?.ToString());
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(_root, path)));
        Assert.Equal("alpha\nbeta\n", await File.ReadAllTextAsync(Path.Combine(_root, "file.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDotNetEditArgumentRepresentationsRemainCallable(bool anonymous)
    {
        object edits = anonymous ? new[] { new { oldText = "alpha", newText = "ALPHA" } } :
            new[] { new Dictionary<string, string> { ["oldText"] = "alpha", ["newText"] = "ALPHA" } };

        var result = await Function("edit").InvokeAsync(new AIFunctionArguments
        {
            ["path"] = "file.txt",
            ["edits"] = edits
        });

        Assert.True(ToolResultOutput.TryRead(result, out var text, out _));
        Assert.Equal("Successfully replaced 1 block(s) in file.txt.", text);
        Assert.Equal("ALPHA\nbeta\n", await File.ReadAllTextAsync(Path.Combine(_root, "file.txt")));
    }

    [Theory]
    [MemberData(nameof(ValidationCases))]
    public async Task InvalidArgumentsUsePinnedErrorsAndHaveNoSideEffects(string caseId, string tool,
        string input, string expected)
    {
        using var document = JsonDocument.Parse(input);
        var arguments = new AIFunctionArguments(document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () => await Function(tool).InvokeAsync(arguments));

        Assert.Equal(expected, failure.Message);
        Assert.Equal("alpha\nbeta\n", await File.ReadAllTextAsync(Path.Combine(_root, "file.txt")));
        Assert.Equal(["file.txt"], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));
        Assert.NotEmpty(caseId);
    }

    [Theory]
    [InlineData("{\"oldText\":\"alpha\",\"newText\":\"ALPHA\"}")]
    [InlineData("{\"edits\":\"[{\\\"oldText\\\":\\\"alpha\\\",\\\"newText\\\":\\\"ALPHA\\\"}]\"}")]
    [InlineData("{\"edits\":{\"oldText\":\"alpha\",\"newText\":\"ALPHA\"}}")]
    public async Task EditPreparesPinnedLegacyAndModelArgumentForms(string input)
    {
        using var document = JsonDocument.Parse(input);
        var arguments = new AIFunctionArguments(document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()))
        {
            ["path"] = "file.txt"
        };

        var result = await Function("edit").InvokeAsync(arguments);

        Assert.True(ToolResultOutput.TryRead(result, out var text, out _));
        Assert.Equal("Successfully replaced 1 block(s) in file.txt.", text);
        Assert.Equal("ALPHA\nbeta\n", await File.ReadAllTextAsync(Path.Combine(_root, "file.txt")));
    }

    [Theory]
    [InlineData("read", "{\"path\":\"file.txt\",\"offset\":null,\"limit\":null}", "alpha\nbeta\n")]
    [InlineData("grep", "{\"pattern\":\"alpha\",\"ignoreCase\":null,\"literal\":null,\"context\":null}", "file.txt:1: alpha")]
    [InlineData("find", "{\"pattern\":\"*.txt\",\"path\":null,\"limit\":null}", "file.txt")]
    [InlineData("ls", "{\"path\":null,\"limit\":null}", "file.txt")]
    public async Task OptionalNullArgumentsUsePinnedDefaults(string tool, string input, string expected)
    {
        using var document = JsonDocument.Parse(input);
        var arguments = new AIFunctionArguments(document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));

        var result = await Function(tool).InvokeAsync(arguments);
        var text = ToolResultOutput.TryRead(result, out var structuredText, out _) ? structuredText : result?.ToString();

        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task ExtraPropertiesRemainAllowedByThePinnedBuiltinSchemas()
    {
        var result = await Function("write").InvokeAsync(new AIFunctionArguments
        {
            ["path"] = "file.txt",
            ["content"] = "replacement",
            ["unusedProperty"] = true
        });

        Assert.Equal("Successfully wrote to file.txt", result?.ToString());
        Assert.Equal("replacement", await File.ReadAllTextAsync(Path.Combine(_root, "file.txt")));
    }

    private AIFunction Function(string name) =>
        new CodingTools(_root).CreateAll().OfType<AIFunction>().Single(function => function.Name == name);

    private static JsonDocument Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory?.FullName ??
            throw new DirectoryNotFoundException("PiSharp.slnx not found."), "docs", "parity", "fixtures", name)));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
