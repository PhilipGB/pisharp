using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Authentication;
using PiSharp.Cli;
using PiSharp.Runtime.Mcp;

namespace PiSharp.Tests;

public sealed class McpCredentialIsolationTests : IDisposable
{
    private static readonly Uri s_url = new("https://mcp.example.com/mcp");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-accounts-" + Guid.NewGuid().ToString("N"));

    public McpCredentialIsolationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SameUrlKeepsSeparateAccountsAndLogoutRemovesOnlySelectedAccount()
    {
        var cache = new McpTokenCache(_root);
        await cache.ForServer("work", s_url).StoreTokensAsync(Tokens("work"), default);
        await cache.ForServer("personal", s_url).StoreTokensAsync(Tokens("personal"), default);

        Assert.Equal("work", (await cache.ForServer("work", s_url).GetTokensAsync(default))?.AccessToken);
        Assert.Equal("personal", (await cache.ForServer("personal", s_url).GetTokensAsync(default))?.AccessToken);
        Assert.True(await cache.RemoveAsync("work", s_url));
        Assert.Null(await cache.ForServer("work", s_url).GetTokensAsync(default));
        Assert.Equal("personal", (await cache.ForServer("personal", s_url).GetTokensAsync(default))?.AccessToken);
        Assert.Equal(["mcp__personal|" + s_url.AbsoluteUri], await StoredKeysAsync());
    }

    [Fact]
    public async Task LegacyCredentialsMoveToFirstReaderAndNormalizeServerName()
    {
        await SeedLegacyAsync();
        var cache = new McpTokenCache(_root);

        Assert.Equal("legacy", (await cache.ForServer("my_work", s_url).GetTokensAsync(default))?.AccessToken);
        Assert.Equal("legacy", (await cache.ForServer("my-work", s_url).GetTokensAsync(default))?.AccessToken);
        Assert.Null(await cache.ForServer("personal", s_url).GetTokensAsync(default));
        Assert.Equal(["mcp__my_work|" + s_url.AbsoluteUri], await StoredKeysAsync());
    }

    [Fact]
    public async Task ConcurrentReadersAssignLegacyCredentialsToOnlyOneAccount()
    {
        await SeedLegacyAsync();
        var loaded = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
            await new McpTokenCache(_root).ForServer("account" + index, s_url).GetTokensAsync(default)));

        Assert.Single(loaded, tokens => tokens is not null);
        Assert.Single(await StoredKeysAsync());
        Assert.DoesNotContain(s_url.AbsoluteUri, await StoredKeysAsync());
    }

    [Fact]
    public async Task LogoutRemovesLegacyCredentialsWithoutMigratingThem()
    {
        await SeedLegacyAsync();
        var cache = new McpTokenCache(_root);

        Assert.True(await cache.RemoveAsync("work", s_url));
        Assert.Empty(await StoredKeysAsync());
        Assert.False(await cache.RemoveAsync("work", s_url));
    }

    [Fact]
    public async Task TokenObservationDoesNotClaimLegacyAccountAndExistingAccountWins()
    {
        await SeedLegacyAsync();
        var cache = new McpTokenCache(_root);
        var work = cache.ForServerWithRefresh("work", s_url);

        Assert.Equal("legacy", (await cache.ReadTokensAsync(work.Key, work.LegacyKey, default))?.AccessToken);
        Assert.Equal([s_url.AbsoluteUri], await StoredKeysAsync());
        await work.StoreTokensAsync(Tokens("work"), default);
        Assert.Equal("work", (await work.GetTokensAsync(default))?.AccessToken);
        Assert.Equal("legacy", (await cache.ForServer("personal", s_url).GetTokensAsync(default))?.AccessToken);
        Assert.Equal(["mcp__personal|" + s_url.AbsoluteUri, "mcp__work|" + s_url.AbsoluteUri], await StoredKeysAsync());
    }

    [Fact]
    public async Task RefreshLocksAreIsolatedByAccountEvenAtTheSameUrl()
    {
        var cache = new McpTokenCache(_root);
        var work = cache.ForServerWithRefresh("work", s_url);
        var personal = cache.ForServerWithRefresh("personal", s_url);
        await using var held = await cache.AcquireRefreshLockAsync(work.Key, default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await using var independent = await new McpTokenCache(_root)
            .AcquireRefreshLockAsync(personal.Key, deadline.Token);
        using var sameAccountDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cache.AcquireRefreshLockAsync(work.Key, sameAccountDeadline.Token));
    }

    [Fact]
    public async Task RefreshUsesTheSelectedAccountAndPersistsOnlyItsRotatedToken()
    {
        var cache = new McpTokenCache(_root);
        await cache.ForServer("work", s_url).StoreTokensAsync(Tokens("work"), default);
        await cache.ForServer("personal", s_url).StoreTokensAsync(Tokens("personal"), default);
        var personal = cache.ForServerWithRefresh("personal", s_url);
        await personal.GetTokensAsync(default);
        var endpoint = new AccountTokenEndpoint();
        var handler = new McpOAuthRefreshHandler(personal, endpoint);
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://auth.example.com/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = "personal-refresh"
            })
        };

        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("refresh_token=personal-refresh", endpoint.Form);
        await personal.StoreTokensAsync(Tokens("personal-rotated"), default);
        await handler.WaitForSettledAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("work", (await cache.ForServer("work", s_url).GetTokensAsync(default))?.AccessToken);
        Assert.Equal("personal-rotated", (await personal.GetTokensAsync(default))?.AccessToken);
    }

    [Fact]
    public async Task ConfiguredCliLogoutKeepsAnotherAccountOnTheSameUrl()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "mcp.json"), JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["work"] = new { url = s_url.AbsoluteUri },
                ["personal"] = new { url = s_url.AbsoluteUri }
            }
        }));
        var cache = new McpTokenCache(_root);
        await cache.ForServer("work", s_url).StoreTokensAsync(Tokens("work"), default);
        await cache.ForServer("personal", s_url).StoreTokensAsync(Tokens("personal"), default);
        var output = new StringWriter();
        var errors = new StringWriter();

        Assert.Equal(0, await McpCommand.RunAsync(["logout", "work"], _root, _root, output, errors));

        Assert.Contains("Signed out", output.ToString());
        Assert.Equal("", errors.ToString());
        Assert.Null(await cache.ForServer("work", s_url).GetTokensAsync(default));
        Assert.Equal("personal", (await cache.ForServer("personal", s_url).GetTokensAsync(default))?.AccessToken);
    }

    private async Task SeedLegacyAsync() => await File.WriteAllTextAsync(Path.Combine(_root, "mcp-auth.json"),
        JsonSerializer.Serialize(new Dictionary<string, TokenContainer> { [s_url.AbsoluteUri] = Tokens("legacy") }));

    private async Task<string[]> StoredKeysAsync()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "mcp-auth.json")));
        return document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
    }

    private static TokenContainer Tokens(string account) => new()
    {
        TokenType = "Bearer",
        AccessToken = account,
        RefreshToken = account + "-refresh",
        ObtainedAt = DateTimeOffset.UtcNow
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class AccountTokenEndpoint : HttpMessageHandler
    {
        public string? Form { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Form = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"personal-rotated","refresh_token":"personal-rotated-refresh","token_type":"Bearer"}""",
                    Encoding.UTF8, "application/json")
            };
        }
    }
}
