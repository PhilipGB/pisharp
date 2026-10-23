using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BuiltinGrepParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "parity", "fixtures",
            "builtin-coding-tool-grep-results-2026-10-22.json")));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("caseId").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RegisteredFunctionMatchesPinnedResultWithoutChangingFiles(string caseId, string fixture)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var document = JsonDocument.Parse(fixture);
        var setup = document.RootElement.GetProperty("setup");
        var root = Path.Combine(Path.GetTempPath(), "pisharp-grep-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var protectedPaths = new List<string>();
        var hashes = new Dictionary<string, byte[]>();
        try
        {
            foreach (var file in setup.GetProperty("files").EnumerateObject())
            {
                var path = Path.Combine(root, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var bytes = Bytes(file.Value);
                await File.WriteAllBytesAsync(path, bytes);
                hashes[path] = SHA256.HashData(bytes);
            }
            if (setup.TryGetProperty("generatedFiles", out var generated))
            {
                var directory = Path.Combine(root, generated.GetProperty("directory").GetString()!);
                Directory.CreateDirectory(directory);
                for (var index = 0; index < generated.GetProperty("count").GetInt32(); index++)
                    await File.WriteAllTextAsync(Path.Combine(directory, index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".txt"), generated.GetProperty("text").GetString());
            }
            if (setup.TryGetProperty("gitDirectories", out var repositories))
                foreach (var repository in repositories.EnumerateArray())
                {
                    using var git = Process.Start(new ProcessStartInfo("git") { WorkingDirectory = Path.Combine(root, repository.GetString()!), ArgumentList = { "init", "-q" } });
                    Assert.NotNull(git);
                    await git.WaitForExitAsync();
                    Assert.Equal(0, git.ExitCode);
                }
            if (setup.TryGetProperty("symlinks", out var links))
                foreach (var link in links.EnumerateObject()) File.CreateSymbolicLink(Path.Combine(root, link.Name), link.Value.GetString()!);
            if (setup.TryGetProperty("modes", out var modes))
                foreach (var entry in modes.EnumerateObject())
                {
                    var path = Path.Combine(root, entry.Name);
                    File.SetUnixFileMode(path, (UnixFileMode)entry.Value.GetInt32());
                    protectedPaths.Add(path);
                }
            var function = new CodingTools(root).CreateAll().OfType<AIFunction>().Single(tool => tool.Name == "grep");
            var arguments = new AIFunctionArguments(setup.GetProperty("arguments").EnumerateObject().ToDictionary(
                item => item.Name, item => item.Value.ValueKind == JsonValueKind.String
                    ? (object?)item.Value.GetString()!.Replace("{WORKSPACE}", root, StringComparison.Ordinal) : item.Value.Clone()));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (setup.TryGetProperty("preCancelled", out var preCancelled) && preCancelled.GetBoolean()) cancellation.Cancel();
            var expected = document.RootElement.GetProperty("expectedResult");
            string text;
            object? details = null;
            try
            {
                var value = await function.InvokeAsync(arguments, cancellation.Token);
                Assert.False(expected.GetProperty("rejected").GetBoolean());
                Assert.True(ToolResultOutput.TryRead(value, out text, out details), caseId);
            }
            catch (Exception error) when (error is not Xunit.Sdk.XunitException)
            {
                Assert.True(expected.GetProperty("rejected").GetBoolean(), error.ToString());
                text = error.Message;
            }
            var actualText = text.Replace(root, "<workspace>", StringComparison.Ordinal);
            if (document.RootElement.TryGetProperty("additionalPinnedTexts", out var variants))
                Assert.Contains(actualText, variants.EnumerateArray().Select(value => value.GetString())
                    .Append(expected.GetProperty("content")[0].GetProperty("text").GetString()));
            else Assert.Equal(expected.GetProperty("content")[0].GetProperty("text").GetString(), actualText);
            using var actualDetails = JsonDocument.Parse(JsonSerializer.Serialize(details, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Replace(root, "<workspace>", StringComparison.Ordinal));
            Assert.True(JsonElement.DeepEquals(expected.GetProperty("details"), actualDetails.RootElement), caseId + " details");
            foreach (var path in protectedPaths) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var (path, hash) in hashes) Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }
        finally
        {
            foreach (var path in protectedPaths) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Bytes(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return Encoding.UTF8.GetBytes(value.GetString()!);
        if (value.TryGetProperty("base64", out var encoded)) return Convert.FromBase64String(encoded.GetString()!);
        if (value.TryGetProperty("repeat", out var repeat)) return Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(repeat.GetString(), value.GetProperty("count").GetInt32())));
        return value.GetProperty("concat").EnumerateArray().SelectMany(Bytes).ToArray();
    }
}
