using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class ExternalEditorTests
{
    [Fact]
    public async Task EditsFromAQuotedCommandInAPrivateTemporaryFileAndCleansItUp()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp external editor " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "fake editor.sh");
            var capture = Path.Combine(root, "capture");
            await File.WriteAllTextAsync(script, "#!/bin/sh\nfile=\"$2\"\nprintf '%s' \"$file\" > \"$1.path\"\ncat \"$file\" > \"$1.input\"\nstat -c '%a' \"$file\" > \"$1.mode\"\nstat -c '%a' \"$(dirname \"$file\")\" > \"$1.directory-mode\"\nprintf 'edited\\r\\n' > \"$file\"\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var command = $"/bin/sh \"{script}\" '{capture}'";

            var result = await ExternalEditor.EditAsync("original draft", command);

            Assert.True(result.Success);
            Assert.Equal("edited", result.Content);
            var editedPath = await File.ReadAllTextAsync(capture + ".path");
            var editedDirectory = Path.GetDirectoryName(editedPath)!;
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "pisharp-editor-"),
                editedDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            Assert.Equal("prompt.md", Path.GetFileName(editedPath));
            Assert.Equal("original draft", await File.ReadAllTextAsync(capture + ".input"));
            Assert.Equal("600", (await File.ReadAllTextAsync(capture + ".mode")).Trim());
            Assert.Equal("700", (await File.ReadAllTextAsync(capture + ".directory-mode")).Trim());
            Assert.False(File.Exists(editedPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(editedPath)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FailingEditorPreservesDraftAndCleansTemporaryFile()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-external-editor-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "fail.sh");
            var capture = Path.Combine(root, "capture");
            await File.WriteAllTextAsync(script, "#!/bin/sh\nprintf '%s' \"$2\" > \"$1\"\nexit 7\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await ExternalEditor.EditAsync("original", $"/bin/sh '{script}' '{capture}'");

            Assert.False(result.Success);
            Assert.Equal(7, result.ExitCode);
            Assert.Equal("original", result.Content);
            var editedPath = await File.ReadAllTextAsync(capture);
            Assert.False(File.Exists(editedPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(editedPath)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolvesConfiguredEnvironmentAndPlatformDefaultsInOrder()
    {
        string? Get(string key) => key switch { "VISUAL" => "visual --wait", "EDITOR" => "editor", _ => null };

        Assert.Equal("configured", ExternalEditor.ResolveCommand(" configured ", Get));
        Assert.Equal("visual --wait", ExternalEditor.ResolveCommand(null, Get));
        Assert.Equal("editor", ExternalEditor.ResolveCommand(" ", key => key == "EDITOR" ? "editor" : null));
        Assert.Equal(OperatingSystem.IsWindows() ? "notepad" : "nano", ExternalEditor.ResolveCommand(null, _ => null));
    }

    [Fact]
    public async Task RejectsMalformedCommandInsteadOfPassingItThroughAShell()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ExternalEditor.EditAsync("draft", "editor 'unfinished"));
        await Assert.ThrowsAsync<ArgumentException>(() => ExternalEditor.EditAsync("draft", "editor\ncommand"));
    }
}
