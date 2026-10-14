using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli;
using PiSharp.Cli.Sessions;
using PiSharp.Runtime.Sessions;

using var inputs = JsonDocument.Parse(File.ReadAllText(args[0]));
var work = args[1];
var presentation = inputs.RootElement.GetProperty("presentation");
var sessionText = inputs.RootElement.GetProperty("sessionJsonl").GetString()!;
var flows = new List<object>();
foreach (var scenario in inputs.RootElement.GetProperty("flows").EnumerateArray())
{
    var id = scenario.GetProperty("id").GetString()!;
    var root = Path.Combine(work, "native", id);
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "scenario.json"), scenario.GetRawText());
    Environment.SetEnvironmentVariable("PISHARP_SHARE_FIXTURE_ROOT", root);
    var paths = new List<string>();
    var progress = new List<string>();
    var requests = new List<object>();
    var credentialReads = 0;
    var htmlExports = 0;
    var jsonlExports = 0;
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    using var http = new HttpClient(new Handler(async (request, ct) =>
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        requests.Add(new { path = request.RequestUri!.PathAndQuery, method = request.Method.Method,
            authorization = request.Headers.Authorization!.ToString(), contentType = request.Content.Headers.ContentType!.ToString(),
            correctLength = request.Content.Headers.ContentLength == System.Text.Encoding.UTF8.GetByteCount(body),
            records = Normalize(body) });
        if (Get(scenario, "cancel") == "radius") cancellation.Cancel();
        ct.ThrowIfCancellationRequested();
        if (Get(scenario, "networkError") is { } networkError) throw new HttpRequestException(networkError);
        return new HttpResponseMessage((HttpStatusCode)(scenario.TryGetProperty("status", out var status) ? status.GetInt32() : 200))
        { Content = new StringContent(Get(scenario, "body") ?? "{\"artifact\":{\"canonical_url\":\"https://radius.example/shared\"}}") };
    }));
    var share = new SessionShare(http, new Uri("https://radius.pi.dev/"), _ =>
    {
        credentialReads++;
        if (Get(scenario, "credentialError") is { } error) throw new InvalidOperationException(error);
        return Task.FromResult(Get(scenario, "token"));
    }, async (path, ct) =>
    {
        jsonlExports++;
        paths.Add(path);
        if (Get(scenario, "exportError") is { } error) throw new IOException(error);
        await File.WriteAllTextAsync(path, SessionShareExport.ExportBranch(PiJsonlSessionInterchange.Import(sessionText), presentation), ct);
    }, async (path, ct) =>
    {
        htmlExports++;
        paths.Add(path);
        if (Get(scenario, "htmlError") is { } error) throw new IOException(error);
        await File.WriteAllTextAsync(path, "fixture html", ct);
    }, scenario.TryGetProperty("missingGithub", out var missing) && missing.GetBoolean()
        ? Path.Combine(root, "missing-gh") : Path.Combine(work, "bin", "gh"), Get(scenario, "viewer") ?? "https://pi.dev/session/");
    Task? cancelChild = null;
    if (Get(scenario, "cancel") == "gist")
    {
        cancelChild = Task.Run(async () =>
        {
            while (!File.Exists(Path.Combine(root, "gist-pid"))) await Task.Delay(5, cancellation.Token);
            cancellation.Cancel();
        });
    }
    SessionShareResult? result = null;
    string? thrown = null;
    try
    {
        result = await share.ShareAsync(Get(scenario, "destination")!, (label, operation) =>
        { progress.Add(label); return operation(cancellation.Token); }, cancellation.Token);
    }
    catch (InvalidOperationException error) { thrown = error.Message; }
    if (cancelChild is not null) await cancelChild;
    var commands = File.Exists(Path.Combine(root, "commands.jsonl"))
        ? File.ReadAllLines(Path.Combine(root, "commands.jsonl")).Select(line =>
        {
            var command = JsonSerializer.Deserialize<string[]>(line)!;
            if (command[0] == "gist") command[^1] = "<export>/session.html";
            return command;
        }).ToArray() : [];
    flows.Add(new { id, message = result?.Message, isError = result?.IsError ?? false, thrown,
        credentialReads, jsonlExports, htmlExports, progress, requests, commands,
        cleaned = paths.All(path => !Directory.Exists(Path.GetDirectoryName(path))),
        cancelledChildExited = Get(scenario, "cancel") != "gist" || ChildExited(root) });
}
var branches = new List<object>();
foreach (var head in inputs.RootElement.GetProperty("heads").EnumerateArray())
{
    var session = PiJsonlSessionInterchange.Import(sessionText);
    session.Tree.Select(head.ValueKind == JsonValueKind.Null ? null : head.GetString());
    var htmlPath = Path.Combine(work, "native-viewer-" + (head.ValueKind == JsonValueKind.Null ? "empty" : head.GetString()) + ".html");
    await SessionExport.ExportHtmlAsync(session, htmlPath, sharePresentation: presentation);
    var html = await File.ReadAllTextAsync(htmlPath);
    const string prefix = "<script id=\"session-data\" type=\"application/json\">";
    var start = html.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
    var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
    branches.Add(new { head = head.ValueKind == JsonValueKind.Null ? null : head.GetString(),
        records = Normalize(SessionShareExport.ExportBranch(session, presentation)),
        viewerData = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(html[start..end]))) });
}
var settings = new List<object>();
foreach (var vector in inputs.RootElement.GetProperty("settings").EnumerateArray())
{
    var root = Path.Combine(work, "native-settings", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(root, "agent"));
    Directory.CreateDirectory(Path.Combine(root, ".pi"));
    await File.WriteAllTextAsync(Path.Combine(root, "agent", "settings.json"), vector.GetProperty("user").GetRawText());
    await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"), vector.GetProperty("project").GetRawText());
    var user = await UserSettings.LoadAsync(Path.Combine(root, "agent"), _ => null);
    var project = await UserSettings.LoadProjectAsync(root);
    var initial = user.Overlay(project).ShareDestination;
    var saved = await UserSettingsPersistence.SaveAsync(Path.Combine(root, "agent"), root, false, true,
        "sessionShareDestination", "radius", user, project, _ => null);
    settings.Add(new { id = vector.GetProperty("id").GetString(), initial, afterSave = saved.Effective.ShareDestination,
        user = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "agent", "settings.json"))) });
}
Console.WriteLine(JsonSerializer.Serialize(new { flows, branches, settings }));

static string? Get(JsonElement scenario, string name) => scenario.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
static JsonNode[] Normalize(string jsonl) => jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
{
    var node = JsonNode.Parse(line)!;
    if ((string?)node["type"] == "session" || (string?)node["customType"] == "pi.share") node["timestamp"] = "<export-time>";
    if ((string?)node["customType"] == "pi.share") node["id"] = "<share-id>";
    return node;
}).ToArray();
static bool ChildExited(string root)
{
    if (!File.Exists(Path.Combine(root, "gist-pid"))) return false;
    try { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(root, "gist-pid")))); return child.HasExited; }
    catch (ArgumentException) { return true; }
}
sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
}
