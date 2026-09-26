using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class PiSessionImportServiceTests
{
    [Fact]
    public async Task ImportsOnlyTheActiveProjectAndCreatesTheConfiguredSessionPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pi-import-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var input = Path.Combine(root, "pi-session.jsonl");
        await File.WriteAllTextAsync(input, SessionJsonl(project));
        try
        {
            var store = new ConversationStore(project, Path.Combine(root, "stored"));
            var service = new PiSessionImportService(store, project, noSession: false);

            var imported = service.ImportFile(input);
            var destination = service.CreateDestinationPath(imported);

            Assert.Equal("pi-session", imported.Id);
            Assert.Equal(project, imported.WorkingDirectory);
            Assert.NotNull(destination);
            Assert.StartsWith(store.DirectoryPath, destination!, StringComparison.Ordinal);
            Assert.EndsWith(".session.json", destination, StringComparison.Ordinal);
            Assert.False(File.Exists(destination));
            Assert.Null(new PiSessionImportService(store, project, noSession: true).CreateDestinationPath(imported));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RejectsDifferentProjectDirectoriesAndMissingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pi-import-boundary-" + Guid.NewGuid().ToString("N"));
        var activeProject = Path.Combine(root, "active");
        var sourceProject = Path.Combine(root, "source");
        Directory.CreateDirectory(activeProject);
        Directory.CreateDirectory(sourceProject);
        var input = Path.Combine(root, "pi-session.jsonl");
        await File.WriteAllTextAsync(input, SessionJsonl(sourceProject));
        try
        {
            var service = new PiSessionImportService(new ConversationStore(activeProject, Path.Combine(root, "stored")),
                activeProject, noSession: false);
            var mismatch = Assert.Throws<InvalidDataException>(() => service.ImportFile(input));
            Assert.Contains("differs from this project", mismatch.Message, StringComparison.Ordinal);
            Assert.Throws<FileNotFoundException>(() => service.ImportFile(Path.Combine(root, "missing.jsonl")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string SessionJsonl(string workingDirectory) => string.Join('\n',
        JsonSerializer.Serialize(new { type = "session", version = 3, id = "pi-session", timestamp = "2026-09-26T00:00:00Z", cwd = workingDirectory }),
        JsonSerializer.Serialize(new
        {
            type = "message",
            id = "entry-1",
            parentId = (string?)null,
            timestamp = "2026-09-26T00:00:01Z",
            message = new { role = "user", content = "hello", timestamp = 1790380801000L }
        }), "");
}
