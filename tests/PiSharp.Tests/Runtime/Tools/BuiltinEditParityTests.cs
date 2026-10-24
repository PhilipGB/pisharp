using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BuiltinEditParityTests
{
    [Fact]
    public async Task AtomicParentPermissionFailureUsesRequestedPathAndKeepsOriginalBytes()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-edit-parent-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(root, "dir");
        Directory.CreateDirectory(parent);
        var target = Path.Combine(parent, "file");
        try
        {
            await File.WriteAllTextAsync(target, "alpha\n");
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var error = await Assert.ThrowsAsync<ToolFailureException>(() => new CodingTools(root).Edit("dir/file", "alpha", "ALPHA"));

            Assert.Equal($"EACCES: permission denied, open '{target}'", error.Message);
            Assert.Equal("alpha\n", await File.ReadAllTextAsync(target));
            Assert.Empty(Directory.EnumerateFiles(parent, ".pisharp-*.tmp"));
        }
        finally
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }

    public static IEnumerable<object[]> Cases()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "parity", "fixtures",
            "builtin-coding-tool-edit-results-2026-10-22.json")));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("caseId").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PinnedResultsDiffDetailsAndFinalFileBytesMatch(string caseId, string fixture)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var document = JsonDocument.Parse(fixture);
        Assert.Equal(caseId, document.RootElement.GetProperty("caseId").GetString());
        var setup = document.RootElement.GetProperty("setup");
        var root = Path.Combine(Path.GetTempPath(), "pisharp-edit-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var protectedPaths = new List<string>();
        try
        {
            if (setup.TryGetProperty("directories", out var directories))
                foreach (var item in directories.EnumerateObject()) Directory.CreateDirectory(Path.Combine(root, item.Name));
            foreach (var file in setup.GetProperty("files").EnumerateObject())
            {
                var path = Path.Combine(root, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, file.Value.ValueKind == JsonValueKind.String
                    ? Encoding.UTF8.GetBytes(file.Value.GetString()!) : Convert.FromBase64String(file.Value.GetProperty("base64").GetString()!));
            }
            if (setup.TryGetProperty("symlinks", out var links))
                foreach (var link in links.EnumerateObject()) File.CreateSymbolicLink(Path.Combine(root, link.Name), link.Value.GetString()!);
            if (setup.TryGetProperty("modes", out var modes))
                foreach (var mode in modes.EnumerateObject())
                {
                    var path = Path.Combine(root, mode.Name);
                    File.SetUnixFileMode(path, (UnixFileMode)mode.Value.GetInt32());
                    protectedPaths.Add(path);
                }
            if (setup.TryGetProperty("directories", out directories))
                foreach (var mode in directories.EnumerateObject())
                {
                    var path = Path.Combine(root, mode.Name);
                    File.SetUnixFileMode(path, (UnixFileMode)mode.Value.GetInt32());
                    protectedPaths.Add(path);
                }
            var arguments = new AIFunctionArguments(setup.GetProperty("arguments").EnumerateObject()
                .ToDictionary(item => item.Name, item => item.Value.ValueKind == JsonValueKind.String
                    ? (object?)item.Value.GetString()!.Replace("{WORKSPACE}", root, StringComparison.Ordinal) : item.Value.Clone()));
            var function = new CodingTools(root).CreateAll().OfType<AIFunction>().Single(tool => tool.Name == "edit");
            using var cancellation = new CancellationTokenSource();
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
            Assert.Equal(expected.GetProperty("content")[0].GetProperty("text").GetString(), text.Replace(root, "<workspace>", StringComparison.Ordinal));
            using var actualDetails = JsonDocument.Parse(JsonSerializer.Serialize(details,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)).Replace(root, "<workspace>", StringComparison.Ordinal));
            Assert.True(JsonElement.DeepEquals(expected.GetProperty("details"), actualDetails.RootElement), caseId + " details");

            foreach (var path in protectedPaths) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var actual = new Dictionary<string, object> { ["."] = new { kind = "directory" } };
            Visit(new DirectoryInfo(root));
            var expectedFiles = document.RootElement.GetProperty("expectedFilesystem");
            Assert.Equal(expectedFiles.EnumerateObject().Select(item => item.Name).Order(), actual.Keys.Order());
            foreach (var item in expectedFiles.EnumerateObject())
            {
                using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(actual[item.Name]));
                Assert.Equal(item.Value.GetProperty("kind").GetString(), snapshot.RootElement.GetProperty("kind").GetString());
                foreach (var field in new[] { "sha256", "target" })
                    if (item.Value.TryGetProperty(field, out var expectedValue))
                        Assert.Equal(expectedValue.GetString(), snapshot.RootElement.GetProperty(field).GetString());
            }

            void Visit(DirectoryInfo directory)
            {
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    var relative = Path.GetRelativePath(root, entry.FullName).Replace(Path.DirectorySeparatorChar, '/');
                    if (entry.LinkTarget is { } target) actual[relative] = new { kind = "symlink", target };
                    else if (entry is DirectoryInfo child)
                    {
                        actual[relative] = new { kind = "directory" };
                        Visit(child);
                    }
                    else actual[relative] = new { kind = "file", sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(entry.FullName))) };
                }
            }
        }
        finally
        {
            foreach (var path in protectedPaths) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }
}
