using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PiSharp.Cli.Sessions;

internal sealed record SessionShareResult(string Message, bool IsError = false, string? ShareUrl = null,
    string? GistUrl = null);

/// <summary>Owns one private export and its Radius or GitHub upload, including cancellation and cleanup.</summary>
internal sealed class SessionShare(HttpClient http, Uri radiusGateway,
    Func<CancellationToken, Task<string?>> radiusCredential,
    Func<string, CancellationToken, Task> exportJsonl,
    Func<string, CancellationToken, Task> exportHtml,
    string githubCli = "gh", string viewerBaseUrl = "https://pi.dev/session/")
{
    internal async Task<SessionShareResult> ShareAsync(string destination,
        Func<string, Func<CancellationToken, Task<SessionShareResult>>, Task<SessionShareResult>>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-share-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var jsonlFile = Path.Combine(directory, "session.jsonl");
        var htmlFile = Path.Combine(directory, "session.html");
        try
        {
            try { await exportJsonl(jsonlFile, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { return new("Failed to export session: " + error.Message, true); }
            if (destination != "gist")
            {
                var token = await radiusCredential(cancellationToken);
                if (!string.IsNullOrEmpty(token))
                    return await RunAsync("Uploading to Radius...", ct => UploadRadiusAsync(jsonlFile, token, ct));
                if (destination == "radius")
                    return new("Radius authentication is not configured. Run '/login radius' first.", true);
            }
            try
            {
                var auth = await RunGitHubAsync(["auth", "status"], cancellationToken);
                if (auth.Code != 0) return new("GitHub CLI is not logged in. Run 'gh auth login' first.", true);
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 2)
            {
                // Node's spawnSync reports a missing executable as a failed auth status.
                return new("GitHub CLI is not logged in. Run 'gh auth login' first.", true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                return new("GitHub CLI (gh) is not installed. Install it from https://cli.github.com/", true);
            }
            try { await exportHtml(htmlFile, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { return new("Failed to export session: " + error.Message, true); }
            return await RunAsync("Creating gist...", ct => UploadGistAsync(htmlFile, ct));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new("Share cancelled");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        Task<SessionShareResult> RunAsync(string label, Func<CancellationToken, Task<SessionShareResult>> run) =>
            progress is null ? run(cancellationToken) : progress(label, run);
    }

    private async Task<SessionShareResult> UploadRadiusAsync(string path, string token, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(radiusGateway, "/v1/artifacts?visibility=organization&title=Pi+session"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            JsonDocument? document = null;
            try { document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken); }
            catch (JsonException) { }
            using (document)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = document?.RootElement;
                var artifact = Property(root, "artifact");
                if (!response.IsSuccessStatusCode || !IsTruthy(artifact))
                {
                    var error = Property(root, "error");
                    var detail = IsTruthy(error) ? FormatValue(error) : string.IsNullOrEmpty(response.ReasonPhrase)
                        ? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) : response.ReasonPhrase;
                    return new("Failed to upload Radius artifact: " + detail, true);
                }
                var url = FormatValue(Property(artifact, "canonical_url"));
                return new("Share URL: " + url, ShareUrl: url);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new("Share cancelled"); }
        catch (Exception error) { return new("Failed to upload Radius artifact: " + error.Message, true); }
    }

    private static JsonElement Property(JsonElement? value, string name) =>
        value is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty(name, out var property) ? property : default;

    private static bool IsTruthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.String => value.GetString()!.Length > 0,
        JsonValueKind.Number => value.GetDouble() != 0,
        _ => true
    };

    private static string FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined => "undefined",
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.Array => string.Join(',', value.EnumerateArray().Select(item =>
            item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : FormatValue(item))),
        _ => "[object Object]"
    };

    private async Task<SessionShareResult> UploadGistAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunGitHubAsync(["gist", "create", "--public=false", path], cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Code != 0)
                return new("Failed to create gist: " + (string.IsNullOrWhiteSpace(result.Error) ? "Unknown error" : result.Error.Trim()), true);
            var gistUrl = result.Output.Trim();
            var gistId = gistUrl.Split('/').LastOrDefault();
            if (string.IsNullOrEmpty(gistId)) return new("Failed to parse gist ID from gh output", true);
            var viewer = viewerBaseUrl + "#" + gistId;
            return new($"Share URL: {viewer}\nGist: {gistUrl}", ShareUrl: viewer, GistUrl: gistUrl);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new("Share cancelled"); }
        catch (Exception error) { return new("Failed to create gist: " + error.Message, true); }
    }

    private async Task<(int Code, string Output, string Error)> RunGitHubAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(githubCli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start GitHub CLI.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2));
            throw;
        }
        return (process.ExitCode, await output, await error);
    }
}
