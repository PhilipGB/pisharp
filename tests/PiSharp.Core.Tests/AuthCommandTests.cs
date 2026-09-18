using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Tests;

/// <summary>
/// The "pisharp auth" subcommand (pinned auth-command.ts / auth-check.ts /
/// credential-print.ts): parsing and validation messages, check statuses and exit codes
/// (0 ready, 1 not ready, 2 invalid), --json field order, --credentials emission, and the
/// print-api-key credential resolution rules.
/// </summary>
public sealed class AuthCommandTests : IDisposable
{
    private readonly TempDirectory _temp;
    private readonly string? _previousAgentDir;
    private readonly string? _previousOpenAiKey;

    public AuthCommandTests()
    {
        _temp = TempDirectory.Create();
        // Point the credential store and model catalog at a private agent directory so the
        // tests never touch the real auth.json.
        _previousAgentDir = Environment.GetEnvironmentVariable("PISHARP_AGENT_DIR");
        Environment.SetEnvironmentVariable("PISHARP_AGENT_DIR", _temp.Path);
        _previousOpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PISHARP_AGENT_DIR", _previousAgentDir);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", _previousOpenAiKey);
        _temp.Dispose();
    }

    [Fact]
    public void ParseUnknownCommandThrowsPinnedMessage()
    {
        var exception = Assert.Throws<AuthCommandError>(() =>
            AuthCommands.Parse(["auth", "frobnicate"]));
        Assert.Equal(
            "Unknown auth command \"frobnicate\". Use \"pisharp auth print-api-key\", " +
            "\"pisharp auth print-bearer-token\", or \"pisharp auth check\".",
            exception.Message);
    }

    [Fact]
    public void ParseRejectsCheckOnlyFlagsOnOtherKinds()
    {
        Assert.Equal("--json is only supported by auth check",
            Assert.Throws<AuthCommandError>(() => AuthCommands.Parse(["auth", "print-api-key", "--json"])).Message);
        Assert.Equal("--min-expiry is only supported by print-bearer-token",
            Assert.Throws<AuthCommandError>(
                () => AuthCommands.Parse(["auth", "check", "--min-expiry", "30m"])).Message);
        Assert.Equal("--min-expiry must use a duration such as 30m or 1h",
            Assert.Throws<AuthCommandError>(
                () => AuthCommands.Parse(["auth", "print-bearer-token", "--min-expiry", "30"])).Message);
    }

    [Fact]
    public void ParseMinExpiryDurations()
    {
        var parsed = AuthCommands.Parse(["auth", "print-bearer-token", "--provider", "openai", "--min-expiry", "30m"]);
        Assert.Equal(30 * 60_000, parsed.MinExpiryMs);
        Assert.Equal(["--provider", "openai"], parsed.Args);

        Assert.Equal(1, AuthCommands.Parse(["auth", "print-bearer-token", "--min-expiry", "1ms"]).MinExpiryMs);
        Assert.Equal(2_000, AuthCommands.Parse(["auth", "print-bearer-token", "--min-expiry", "2s"]).MinExpiryMs);
        Assert.Equal(3_600_000, AuthCommands.Parse(["auth", "print-bearer-token", "--min-expiry", "1h"]).MinExpiryMs);
    }

    [Fact]
    public void ValidateArgsRequiresProviderOrModelAndRejectsOtherInputs()
    {
        Assert.Equal(
            "Auth checks require --provider <provider> or --model <model>",
            Assert.Throws<AuthCommandError>(() => AuthCommands.ValidateArgs(EmptyOptions(), AuthCommandKind.Check)).Message);
        Assert.Equal(
            "Credential printing requires --provider <provider> or --model <model>",
            Assert.Throws<AuthCommandError>(() => AuthCommands.ValidateArgs(EmptyOptions(), AuthCommandKind.PrintApiKey)).Message);
        Assert.Equal(
            "Auth commands only accept --provider and --model",
            Assert.Throws<AuthCommandError>(() =>
                AuthCommands.ValidateArgs(OptionsWith(ApiKey: "k"), AuthCommandKind.Check)).Message);
    }

    [Fact]
    public async Task CheckUnknownProviderIsNotReady()
    {
        var (stdout, exit) = await RunAuthAsync(["auth", "check", "--provider", "nope"]);
        Assert.Equal("not_ready", stdout);
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task CheckUnconfiguredProviderIsNotReadyWithReason()
    {
        var (stdout, exit) = await RunAuthAsync(["auth", "check", "--provider", "openai", "--json"]);
        Assert.Equal(1, exit);
        var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("not_ready", root.GetProperty("status").GetString());
        Assert.Equal("openai", root.GetProperty("provider").GetString());
        Assert.Equal("credentials_not_configured", root.GetProperty("reason").GetString());
        Assert.False(root.TryGetProperty("authType", out _));
    }

    [Fact]
    public async Task CheckConfiguredProviderIsReadyAndCredentialsEmitTheKey()
    {
        WriteAuthJson(new Dictionary<string, string> { ["openai"] = "sk-test-123" });

        var (plain, plainExit) = await RunAuthAsync(["auth", "check", "--provider", "openai"]);
        Assert.Equal("ready", plain);
        Assert.Equal(0, plainExit);

        var (json, jsonExit) = await RunAuthAsync(
            ["auth", "check", "--provider", "openai", "--json", "--credentials"]);
        Assert.Equal(0, jsonExit);
        var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("ready", root.GetProperty("status").GetString());
        Assert.Equal("openai", root.GetProperty("provider").GetString());
        Assert.Equal("api_key", root.GetProperty("authType").GetString());
        Assert.Equal("sk-test-123", root.GetProperty("credentials").GetString());

        var (key, keyExit) = await RunAuthAsync(["auth", "check", "--provider", "openai", "--credentials"]);
        Assert.Equal("sk-test-123", key);
        Assert.Equal(0, keyExit);
    }

    [Fact]
    public async Task PrintApiKeyEmitsTheStoredKey()
    {
        WriteAuthJson(new Dictionary<string, string> { ["openai"] = "sk-test-123" });

        var (stdout, exit) = await RunAuthAsync(["auth", "print-api-key", "--provider", "openai"]);
        Assert.Equal("sk-test-123", stdout);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task PrintApiKeyForOAuthProviderReportsTheMismatch()
    {
        WriteAuthJson(new Dictionary<string, string> { ["openai"] = "oauth-placeholder" });

        var (stdout, stderr, exit) = await RunAuthCaptureAsync(["auth", "print-api-key", "--provider", "openai"]);
        Assert.Equal(
            "Error: Provider \"openai\" is configured with OAuth, not an API key",
            stderr);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(1, exit);
    }

    [Fact]
    public void GetAuthCredentialPrefersTheApiKeyAndFallsBackToBearer()
    {
        Assert.Equal(
            "sk-abc",
            AuthCommands.GetAuthCredential(new AuthResult
            {
                Auth = new ModelAuth { ApiKey = "sk-abc" },
                Source = "test",
            }));

        Assert.Equal(
            "tok-123",
            AuthCommands.GetAuthCredential(new AuthResult
            {
                Auth = new ModelAuth
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer tok-123",
                    },
                },
                Source = "test",
            }));

        Assert.Null(AuthCommands.GetAuthCredential(null));
        Assert.Null(AuthCommands.GetAuthCredential(new AuthResult { Auth = new ModelAuth(), Source = "test" }));
    }

    [Fact]
    public async Task PrintApiKeyWithUnknownProviderReportsUnknown()
    {
        var (_, stderr, exit) = await RunAuthCaptureAsync(["auth", "print-api-key", "--provider", "nope"]);
        Assert.Equal("Error: Unknown provider \"nope\". Use --list-models to see available providers.", stderr);
        Assert.Equal(1, exit);
    }

    private async Task<(string Stdout, int Exit)> RunAuthAsync(string[] args)
    {
        var (stdout, _, exit) = await RunAuthCaptureAsync(args);
        return (stdout, exit);
    }

    private async Task<(string Stdout, string Stderr, int Exit)> RunAuthCaptureAsync(string[] args)
    {
        var command = AuthCommands.Parse(args);
        var options = CliOptions.ParseWithoutEnvironment(command.Args);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await AuthCommands.RunAsync(command, options, CancellationToken.None);
            // The pinned CLI writes exactly one trailing newline; strip it for assertions.
            return (stdout.ToString().TrimEnd('\r', '\n'), stderr.ToString().TrimEnd('\r', '\n'), exit);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    private static void WriteAuthJson(Dictionary<string, string> apiKeys)
    {
        var data = new Dictionary<string, Credential>();
        foreach (var (provider, key) in apiKeys)
        {
            data[provider] = key.StartsWith("oauth-")
                ? new OAuthCredential { Access = "a", Refresh = "r", Expires = long.MaxValue }
                : new ApiKeyCredential(key);
        }

        var store = new FileCredentialStore(Path.Combine(
            Environment.GetEnvironmentVariable("PISHARP_AGENT_DIR")!, "auth.json"));
        foreach (var (provider, credential) in data)
        {
            store.ModifyAsync(provider, _ => Task.FromResult<Credential?>(credential), CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private static CliOptions EmptyOptions() => CliOptions.ParseWithoutEnvironment([]);

    private static CliOptions OptionsWith(string? ApiKey = null) => CliOptions.ParseWithoutEnvironment(
        ApiKey is null ? [] : ["--api-key", ApiKey]);
}
