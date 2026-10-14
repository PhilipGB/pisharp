using System.Diagnostics;
using System.Net;
using System.Text;
using PiSharp.Cli.Sessions;

namespace PiSharp.Tests;

public sealed class SessionShareTests
{
    [Fact]
    public async Task ForcedGistBypassesRadiusAndCreatesAPrivateShare()
    {
        using var fixture = new SessionShareFixture { RejectCredentialLookup = true };
        var result = await fixture.Create().ShareAsync("gist");
        Assert.False(result.IsError);
        Assert.Equal("https://pi.dev/session/#fixture-id", result.ShareUrl);
        Assert.Equal("https://gist.github.com/fixture/fixture-id", result.GistUrl);
        Assert.Equal(0, fixture.CredentialReads);
        Assert.Equal(["auth", "status"], fixture.Commands[0]);
        Assert.Equal(["gist", "create", "--public=false", Assert.Single(fixture.HtmlExports)], fixture.Commands[1]);
        Assert.Single(fixture.JsonlExports);
        fixture.AssertExportsCleaned();
    }

    [Fact]
    public async Task ForcedRadiusWithoutAuthenticationDoesNotFallBackToGist()
    {
        using var fixture = new SessionShareFixture();
        var result = await fixture.Create().ShareAsync("radius");
        Assert.True(result.IsError);
        Assert.Equal("Radius authentication is not configured. Run '/login radius' first.", result.Message);
        Assert.Empty(fixture.Commands);
        Assert.Empty(fixture.HtmlExports);
        fixture.AssertExportsCleaned();
    }

    [Fact]
    public async Task AutoWithoutRadiusAuthenticationUsesGistAndHonorsViewerOverride()
    {
        using var fixture = new SessionShareFixture();
        var result = await fixture.Create("https://viewer.example/sessions/").ShareAsync("auto");
        Assert.Equal("https://viewer.example/sessions/#fixture-id", result.ShareUrl);
        Assert.Equal(1, fixture.CredentialReads);
        Assert.Equal(2, fixture.Commands.Count);
        fixture.AssertExportsCleaned();
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("radius")]
    public async Task AuthenticatedRadiusUploadsTheBranchWithItsByteLengthAndNoGist(string destination)
    {
        using var fixture = new SessionShareFixture { Token = "fixture-token" };
        fixture.Respond = async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://radius.example/v1/artifacts?visibility=organization&title=Pi+session", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer fixture-token", request.Headers.Authorization!.ToString());
            Assert.Equal("application/x-ndjson", request.Content!.Headers.ContentType!.MediaType);
            var body = await request.Content.ReadAsStringAsync(ct);
            Assert.Contains("héllo", body);
            Assert.Equal(Encoding.UTF8.GetByteCount(body), request.Content.Headers.ContentLength);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"artifact\":{\"canonical_url\":\"https://radius.example/shared\"}}") };
        };
        var labels = new List<string>();
        var result = await fixture.Create().ShareAsync(destination, (label, operation) =>
        {
            labels.Add(label);
            return operation(CancellationToken.None);
        });
        Assert.Equal("https://radius.example/shared", result.ShareUrl);
        Assert.Equal(["Uploading to Radius..."], labels);
        Assert.Empty(fixture.Commands);
        Assert.Empty(fixture.HtmlExports);
        fixture.AssertExportsCleaned();
    }

    [Theory]
    [InlineData(403, "{\"error\":\"denied\"}", "denied")]
    [InlineData(200, "broken JSON", "OK")]
    [InlineData(200, "{}", "OK")]
    [InlineData(200, "{\"artifact\":null}", "OK")]
    [InlineData(200, "{\"artifact\":0}", "OK")]
    [InlineData(200, "{\"artifact\":\"\"}", "OK")]
    [InlineData(403, "{\"error\":123}", "123")]
    public async Task AHandledRadiusFailureNeverFallsBackToGist(int code, string body, string detail)
    {
        using var fixture = new SessionShareFixture { Token = "fixture-token" };
        fixture.Respond = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body) });
        var result = await fixture.Create().ShareAsync("auto");
        Assert.True(result.IsError);
        Assert.Equal("Failed to upload Radius artifact: " + detail, result.Message);
        Assert.Empty(fixture.Commands);
        fixture.AssertExportsCleaned();
    }

    [Fact]
    public async Task RadiusPresentationKeepsPiNullUrlCoercion()
    {
        using var fixture = new SessionShareFixture { Token = "fixture-token" };
        fixture.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"artifact\":{\"canonical_url\":null}}") });
        var result = await fixture.Create().ShareAsync("radius");
        Assert.Equal("Share URL: null", result.Message);
        Assert.False(result.IsError);
        fixture.AssertExportsCleaned();
    }

    [Fact]
    public async Task RadiusCancellationSettlesWithoutGistAndCleansTheExport()
    {
        using var fixture = new SessionShareFixture { Token = "fixture-token" };
        fixture.Respond = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Create().ShareAsync("auto", cancellationToken: cancellation.Token);
        await fixture.RequestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        Assert.Equal(new SessionShareResult("Share cancelled"), await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(fixture.Commands);
        fixture.AssertExportsCleaned();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportFailuresStopBeforeTheirUploadAndPreserveTheDiagnostic(bool html)
    {
        using var fixture = new SessionShareFixture { ExportError = html ? null : "export fixture", HtmlError = html ? "export fixture" : null };
        var result = await fixture.Create().ShareAsync("gist");
        Assert.Equal(new SessionShareResult("Failed to export session: export fixture", true), result);
        Assert.Equal(html ? 1 : 0, fixture.Commands.Count);
        fixture.AssertExportsCleaned();
    }

    [Theory]
    [InlineData(1, "fixture failed", "https://gist.github.com/fixture/id", "Failed to create gist: fixture failed")]
    [InlineData(1, "", "", "Failed to create gist: Unknown error")]
    [InlineData(0, "", "", "Failed to parse gist ID from gh output")]
    [InlineData(0, "", "https://gist.github.com/", "Failed to parse gist ID from gh output")]
    public async Task GistFailureAndMalformedOutputAreReportedAndCleaned(int code, string error, string output, string expected)
    {
        using var fixture = new SessionShareFixture();
        fixture.SetGitHubScenario(new { code, error, output });
        var result = await fixture.Create().ShareAsync("gist");
        Assert.Equal(new SessionShareResult(expected, true), result);
        fixture.AssertExportsCleaned();
    }

    [Fact]
    public async Task FailedGitHubAuthenticationStopsBeforeHtmlExport()
    {
        using var fixture = new SessionShareFixture();
        fixture.SetGitHubScenario(new { authCode = 1 });
        var result = await fixture.Create().ShareAsync("gist");
        Assert.Equal(new SessionShareResult("GitHub CLI is not logged in. Run 'gh auth login' first.", true), result);
        Assert.Empty(fixture.HtmlExports);
        Assert.Single(fixture.Commands);
        fixture.AssertExportsCleaned();
    }

    [Fact]
    public async Task MissingGitHubExecutableKeepsPiFailedAuthStatusBehavior()
    {
        using var fixture = new SessionShareFixture();
        File.Delete(fixture.GitHubPath);
        var result = await fixture.Create().ShareAsync("gist");
        Assert.Equal(new SessionShareResult("GitHub CLI is not logged in. Run 'gh auth login' first.", true), result);
        Assert.Empty(fixture.HtmlExports);
        fixture.AssertExportsCleaned();
    }

    [Fact]
    public async Task GistCancellationKillsTheChildAndCleansTheExport()
    {
        using var fixture = new SessionShareFixture();
        fixture.SetGitHubScenario(new { wait = true });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var pending = fixture.Create().ShareAsync("gist", cancellationToken: cancellation.Token);
        var marker = Path.Combine(fixture.Root, "gist-pid");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(marker))
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(3), "The child did not start.");
            await Task.Delay(10, cancellation.Token);
        }
        var pid = int.Parse(await File.ReadAllTextAsync(marker));
        cancellation.Cancel();
        Assert.Equal(new SessionShareResult("Share cancelled"), await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        fixture.AssertExportsCleaned();
    }

    [Fact]
    public async Task ConcurrentInvocationsUseDifferentPrivateExportDirectories()
    {
        using var fixture = new SessionShareFixture { Token = "fixture-token" };
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        fixture.Respond = async (_, ct) =>
        {
            if (Interlocked.Increment(ref requests) == 2) bothEntered.TrySetResult();
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"artifact\":{\"canonical_url\":\"https://radius.example/share\"}}") };
        };
        var share = fixture.Create();
        var results = await Task.WhenAll(share.ShareAsync("radius"), share.ShareAsync("radius"));
        Assert.All(results, result => Assert.False(result.IsError));
        Assert.Equal(2, fixture.JsonlExports.Distinct().Count());
        fixture.AssertExportsCleaned();
    }
}
