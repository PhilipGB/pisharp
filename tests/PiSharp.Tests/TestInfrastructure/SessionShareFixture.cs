using System.Text.Json;
using PiSharp.Cli.Sessions;

namespace PiSharp.Tests;

internal sealed class SessionShareFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-share-fixture-" + Guid.NewGuid().ToString("N"));
    internal List<string> JsonlExports { get; } = [];
    internal List<string> HtmlExports { get; } = [];
    internal int CredentialReads { get; private set; }
    internal string? Token { get; set; }
    internal bool RejectCredentialLookup { get; set; }
    internal string? ExportError { get; set; }
    internal string? HtmlError { get; set; }
    internal string GitHubPath => Path.Combine(Root, "gh");
    internal TaskCompletionSource RequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("{\"artifact\":{\"canonical_url\":\"https://radius.example/share/fixture\"}}") });
    private readonly HttpClient _http;

    internal SessionShareFixture()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "scenario.json"), "{}");
        var rootLiteral = JsonSerializer.Serialize(Root);
        File.WriteAllText(GitHubPath, $$"""
            #!/usr/bin/env python3
            import json, os, pathlib, sys, time
            root = pathlib.Path({{rootLiteral}})
            scenario = json.loads((root / 'scenario.json').read_text())
            with (root / 'commands.jsonl').open('a') as log:
                log.write(json.dumps(sys.argv[1:]) + '\n')
            if sys.argv[1:3] == ['auth', 'status']:
                sys.exit(scenario.get('authCode', 0))
            (root / 'gist-pid.tmp').write_text(str(os.getpid()))
            (root / 'gist-pid.tmp').replace(root / 'gist-pid')
            if scenario.get('wait'):
                while True: time.sleep(0.02)
            if len(sys.argv) > 4:
                (root / 'uploaded.html').write_text(pathlib.Path(sys.argv[-1]).read_text())
            print(scenario.get('output', 'https://gist.github.com/fixture/fixture-id'))
            print(scenario.get('error', ''), file=sys.stderr)
            sys.exit(scenario.get('code', 0))
            """ + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(GitHubPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _http = new(new Handler(this));
    }

    internal void SetGitHubScenario(object scenario) =>
        File.WriteAllText(Path.Combine(Root, "scenario.json"), JsonSerializer.Serialize(scenario));

    internal IReadOnlyList<string[]> Commands => File.Exists(Path.Combine(Root, "commands.jsonl"))
        ? File.ReadAllLines(Path.Combine(Root, "commands.jsonl")).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray()
        : [];

    internal SessionShare Create(string viewerBaseUrl = "https://pi.dev/session/") => new(_http,
        new Uri("http://radius.example/"), _ =>
        {
            CredentialReads++;
            if (RejectCredentialLookup) throw new InvalidOperationException("unexpected Radius lookup");
            return Task.FromResult(Token);
        }, async (path, ct) =>
        {
            JsonlExports.Add(path);
            if (ExportError is not null) throw new IOException(ExportError);
            await File.WriteAllTextAsync(path, "{\"type\":\"session\",\"title\":\"héllo\"}\n", ct);
        }, async (path, ct) =>
        {
            HtmlExports.Add(path);
            if (HtmlError is not null) throw new IOException(HtmlError);
            await File.WriteAllTextAsync(path, "<!doctype html>fixture", ct);
        }, GitHubPath, viewerBaseUrl);

    internal void AssertExportsCleaned()
    {
        foreach (var path in JsonlExports.Concat(HtmlExports)) Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(Root, true);
    }

    private sealed class Handler(SessionShareFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            fixture.RequestEntered.TrySetResult();
            return fixture.Respond(request, cancellationToken);
        }
    }
}
