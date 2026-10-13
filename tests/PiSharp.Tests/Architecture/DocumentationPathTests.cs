using System.Diagnostics;

namespace PiSharp.Tests;

public sealed class DocumentationPathTests
{
    [Fact]
    public async Task TrackedLinksDirectoriesAndReferenceDefinitionsPass()
    {
        var result = await ValidateAsync(new()
        {
            ["AGENTS.md"] = "Read [Architecture](docs/architecture.md).\n",
            ["docs/architecture.md"] = "[Core](../src/PiSharp.Core/) [Source](../src/PiSharp.Core/Value.cs#member)\n" +
                "`src/PiSharp.Core/Value.cs`\n[Source]: ../src/PiSharp.Core/Value.cs\n[Web](https://example.invalid/a)\n",
            ["src/PiSharp.Core/Value.cs"] = "namespace Fixture;\n"
        });
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Documentation paths verified", result.Output);
        Assert.Equal("", result.Error);
    }

    [Fact]
    public async Task ExistingUntrackedFileCannotSatisfyRepositoryLink()
    {
        var result = await ValidateAsync(new()
        {
            ["docs/architecture.md"] = "[Local evidence](../local.json)\n"
        }, new() { ["local.json"] = "{}\n" });
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("untracked or missing path: ../local.json", result.Error);
    }

    [Fact]
    public async Task OwnInlinePathFailsWhileUpstreamShorthandAndSyntheticInputsAreSeparate()
    {
        var result = await ValidateAsync(new()
        {
            ["docs/architecture.md"] = "`src/PiSharp.Missing/File.cs`\n",
            ["docs/parity/example.md"] = "Pi source: `src/core/agent.ts`, `docs/models.md`.\nSynthetic input: `src/test.ts`.\n"
        });
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("src/PiSharp.Missing/File.cs", result.Error);
        Assert.DoesNotContain("src/core/agent.ts", result.Error);
        Assert.DoesNotContain("docs/models.md", result.Error);
        Assert.DoesNotContain("src/test.ts", result.Error);
    }

    [Fact]
    public async Task TemporaryRefactorDocumentsAreExcluded()
    {
        var result = await ValidateAsync(new()
        {
            ["AGENTS.md"] = "Read `docs/refactor/bad.md`.\n",
            ["docs/refactor/bad.md"] = "[Absent](missing.json)\n"
        });
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> ValidateAsync(
        Dictionary<string, string> tracked, Dictionary<string, string>? untracked = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-doc-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var (path, text) in tracked.Concat(untracked ?? []))
            {
                var file = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, text);
            }
            Assert.Equal(0, (await RunAsync(root, "git", ["init", "--quiet"])).ExitCode);
            Assert.Equal(0, (await RunAsync(root, "git", ["add", "--", .. tracked.Keys])).ExitCode);
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "PiSharp.slnx")))
                repository = repository.Parent;
            Assert.NotNull(repository);
            return await RunAsync(root, "python3",
                [Path.Combine(repository.FullName, "tools", "validate_doc_paths.py"), "--repo", root]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string directory,
        string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }
}
