using PiSharp.Cli;
using PiSharp.Runtime;

namespace PiSharp.Tests;

public sealed class ToolSelectionTests
{
    [Fact]
    public async Task OptInLsMatchesPinnedAsciiListingAndLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-ls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "Beta"));
            foreach (var file in new[] { "z.txt", ".env", "alpha.txt" })
                await File.WriteAllTextAsync(Path.Combine(directory, file), "");
            var tool = new DirectoryListingTool(directory);
            Assert.Equal(".env\nalpha.txt\nBeta/\nz.txt", await tool.List());
            Assert.Equal(".env\nalpha.txt\n\n[2 entries limit reached. Use limit=4 for more]", await tool.List(limit: 2));
            Assert.Equal("(empty directory)", await tool.List(limit: 0));
            Assert.Equal($"Not a directory: {Path.Combine(directory, "z.txt")}",
                (await Assert.ThrowsAsync<PiSharp.Runtime.Tools.ToolFailureException>(() => tool.List("z.txt"))).Message);
            var registry = new CodingTools(directory);
            Assert.Equal(["read", "bash", "edit", "write"], registry.Create().Select(t => t.Name));
            Assert.Equal(["ls"], registry.Create(["ls"]).Select(t => t.Name));
            Assert.Empty(registry.Create(noTools: true));
            Assert.Equal(["ls"], registry.Create(["ls"], noTools: true).Select(t => t.Name));
            Assert.Equal(["read"], registry.Create(["read", "bash"], ["bash"]).Select(t => t.Name));
            Assert.Throws<ArgumentException>(() => registry.Create(["not-a-tool"]));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CliAtFileArgumentsAppendUtf8TextAndEscapeFilename()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-at-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "source & notes.txt");
            await File.WriteAllTextAsync(path, "\uFEFFImportant source", new System.Text.UTF8Encoding(true));
            var cli = CliArguments.Parse(["--print", "Explain", "@source & notes.txt"]);
            Assert.Equal(["source & notes.txt"], cli.FileArguments);
            var prompt = await CliFileArguments.AppendTextFilesAsync(cli.Prompt, cli.FileArguments, root);
            Assert.Equal($"<file name=\"{System.Security.SecurityElement.Escape(path)}\">\nImportant source\n</file>\n\nExplain", prompt);
            await Assert.ThrowsAsync<FileNotFoundException>(() => CliFileArguments.AppendTextFilesAsync("", ["missing.txt"], root));
            await File.WriteAllTextAsync(Path.Combine(root, "empty"), "");
            Assert.Equal("", await CliFileArguments.AppendTextFilesAsync("", ["empty"], root));
            await File.WriteAllBytesAsync(Path.Combine(root, "binary"), [0xff, 0x00]);
            await Assert.ThrowsAsync<InvalidDataException>(() => CliFileArguments.AppendTextFilesAsync("", ["binary"], root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CliAtFileArgumentsCreateImageContentAndRejectMismatchedSignatures()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-at-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = Path.Combine(root, "diagram.png");
            await File.WriteAllBytesAsync(image, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
            var prompt = await CliFileArguments.ProcessFilesAsync("Describe it", ["diagram.png"], root);
            Assert.Equal($"<image name=\"{System.Security.SecurityElement.Escape(image)}\" />\n\nDescribe it", prompt.Text);
            Assert.Single(prompt.Images);
            Assert.Equal("image/png", prompt.Images[0].MediaType);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, prompt.Images[0].Data.ToArray());

            await File.WriteAllBytesAsync(image, [0xff, 0x00]);
            await Assert.ThrowsAsync<InvalidDataException>(() => CliFileArguments.ProcessFilesAsync("", ["diagram.png"], root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MafReceivesOnlyTheSelectedToolLoadout()
    {
        var provider = new ToolCaptureClient();
        var tools = new CodingTools(Path.GetTempPath());
        var agent = new PiAgent(provider, tools, ["ls"], noTools: true);
        var session = await agent.CreateSessionAsync();
        await foreach (var _ in agent.RunStreamingAsync("list", session)) { }
        Assert.Equal(["ls"], provider.ToolNames);
        var empty = new PiAgent(provider, tools, noTools: true);
        await foreach (var _ in empty.RunStreamingAsync("no tools", await empty.CreateSessionAsync())) { }
        Assert.Empty(provider.ToolNames);
        var extension = Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "ok", name: "custom");
        var extensionOnly = new PiAgent(provider, tools, extensionTools: [extension], noBuiltinTools: true);
        await foreach (var _ in extensionOnly.RunStreamingAsync("extension only", await extensionOnly.CreateSessionAsync())) { }
        Assert.Equal(["custom"], provider.ToolNames);
        var allowBuiltin = new PiAgent(provider, tools, selectedTools: ["read"], noBuiltinTools: true);
        await foreach (var _ in allowBuiltin.RunStreamingAsync("explicit builtin", await allowBuiltin.CreateSessionAsync())) { }
        Assert.Equal(["read"], provider.ToolNames);
        var excluded = new PiAgent(provider, tools, excludedTools: ["custom"], extensionTools: [extension], noBuiltinTools: true);
        await foreach (var _ in excluded.RunStreamingAsync("excluded extension", await excluded.CreateSessionAsync())) { }
        Assert.Empty(provider.ToolNames);
    }

    private sealed class ToolCaptureClient : Microsoft.Extensions.AI.IChatClient
    {
        public IReadOnlyList<string> ToolNames { get; private set; } = [];
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ToolNames = options?.Tools?.Select(t => t.Name).ToArray() ?? [];
            yield return new Microsoft.Extensions.AI.ChatResponseUpdate(Microsoft.Extensions.AI.ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void CliToolFiltersAreParsedWithoutConsumingPromptAfterSeparator()
    {
        var args = CliArguments.Parse(["--tools", "read, ls", "--exclude-tools", "ls", "--", "--no-tools"]);
        Assert.Equal(["read", "ls"], args.Tools);
        Assert.Equal(["ls"], args.ExcludeTools);
        Assert.Equal("--no-tools", args.Prompt);
        Assert.True(CliArguments.Parse(["--no-tools"]).NoTools);
        Assert.True(CliArguments.Parse(["--no-builtin-tools"]).NoBuiltinTools);
        Assert.True(CliArguments.Parse(["-nbt", "--tools", "read"]).NoBuiltinTools);
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--tools"]));
    }
}
