using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;

namespace PiSharp.McpConformance;

internal static class Program
{
    private const int MaximumSignIns = 3;
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(20);

    private sealed record ToolCall(string Name, Dictionary<string, object?> Arguments);

    private static async Task<int> Main(string[] args)
    {
        var reportPath = Environment.GetEnvironmentVariable("PI_MCP_CONFORMANCE_REPORT");
        try
        {
            var serverUrl = args.LastOrDefault();
            var scenario = Environment.GetEnvironmentVariable("MCP_CONFORMANCE_SCENARIO");
            if (serverUrl is null || scenario is null || !Uri.TryCreate(serverUrl, UriKind.Absolute, out var endpoint))
                throw new ArgumentException("Usage: MCP_CONFORMANCE_SCENARIO=<scenario> PiSharp.McpConformance <server-url>");

            await RunAsync(endpoint, scenario, ReadContext());
            WriteReport(reportPath, success: true, error: null);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("[pisharp-conformance] error: " + error.Message);
            WriteReport(reportPath, success: false, error: error.Message);
            return 1;
        }
    }

    private static async Task RunAsync(Uri endpoint, string scenario, IReadOnlyDictionary<string, JsonElement> context)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-conformance-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var workingDirectory = Path.Combine(root, "project");
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(workingDirectory);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            var server = CreateServer(endpoint, agentDirectory, context);
            var configuration = new McpConfiguration([server], AutoEnableCodemode: false, Errors: [])
            {
                AgentDirectory = agentDirectory
            };
            using var catalog = ExtensionCatalog.Load(agentDirectory, workingDirectory,
                projectTrusted: false, discover: false);
            var manager = new McpRuntimeManager(agentDirectory);
            Task? browserVisit = null;
            manager.SetAuthorizationUrlPresenter((_, authorizationUrl) =>
            {
                Console.Error.WriteLine($"[pisharp-conformance] authorization URL: {authorizationUrl.GetLeftPart(UriPartial.Authority)}{authorizationUrl.AbsolutePath}");
                browserVisit = VisitAuthorizationUrlAsync(authorizationUrl);
            });

            var startupErrors = await McpRuntime.RegisterAsync(configuration, catalog, workingDirectory,
                deadline.Token, manager);
            var needsSignIn = startupErrors.Where(IsAuthorizationRequired).ToArray();
            var startupFailures = startupErrors.Where(error => !IsAuthorizationRequired(error)).ToArray();
            if (startupFailures.Length > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, startupFailures));

            var signInCount = 0;
            async Task SignInAsync()
            {
                if (signInCount >= MaximumSignIns)
                    throw new InvalidOperationException($"Still requires sign-in after {signInCount} sign-ins");
                signInCount++;
                browserVisit = null;
                Console.Error.WriteLine($"[pisharp-conformance] sign-in {signInCount}");
                var result = await manager.HandleCommandAsync("login " + server.Name, deadline.Token);
                if (browserVisit is not null) await browserVisit.WaitAsync(deadline.Token);
                if (!result.StartsWith("Signed in to MCP server", StringComparison.Ordinal) &&
                    !result.StartsWith("Already connected to MCP server", StringComparison.Ordinal))
                    throw new InvalidOperationException(result);
            }

            if (needsSignIn.Length > 0)
            {
                await SignInAsync();
                while (!HasTool(catalog.Registration, "test-tool"))
                {
                    if (signInCount >= MaximumSignIns)
                        throw new InvalidOperationException($"Still requires sign-in after {signInCount} sign-ins");
                    await SignInAsync();
                }
            }
            Console.Error.WriteLine($"[pisharp-conformance] connected, scenario: {scenario}");

            foreach (var call in ToolCalls(scenario, catalog.Registration))
            {
                while (true)
                {
                    try
                    {
                        var tool = FindTool(catalog.Registration, call.Name);
                        var result = await tool.Function.InvokeAsync(new AIFunctionArguments(call.Arguments), deadline.Token);
                        if (result is PiSharpToolResult { IsError: true } toolError)
                            throw new InvalidOperationException($"Tool {call.Name} failed: {toolError.Text}");
                        Console.Error.WriteLine($"[pisharp-conformance] {call.Name} completed");
                        break;
                    }
                    catch (Exception error) when (ContainsSignInRequired(error))
                    {
                        await SignInAsync();
                    }
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static McpServerConfiguration CreateServer(Uri endpoint, string agentDirectory,
        IReadOnlyDictionary<string, JsonElement> context)
    {
        JsonElement? oauth = null;
        if (context.TryGetValue("client_id", out var clientId) && clientId.ValueKind == JsonValueKind.String)
        {
            var values = new Dictionary<string, string?> { ["clientId"] = clientId.GetString() };
            if (context.TryGetValue("client_secret", out var clientSecret) && clientSecret.ValueKind == JsonValueKind.String)
                values["clientSecret"] = clientSecret.GetString();
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(values));
            oauth = document.RootElement.Clone();
        }

        return new McpServerConfiguration(
            Name: "conformance",
            SourcePath: Path.Combine(agentDirectory, "mcp.json"),
            Scope: "conformance",
            Enabled: true,
            Exposure: McpToolExposure.Direct,
            ToolExposure: new Dictionary<string, McpToolExposure>(StringComparer.Ordinal),
            Timeout: s_requestTimeout,
            Command: null,
            Arguments: [],
            WorkingDirectory: null,
            Environment: new Dictionary<string, string>(StringComparer.Ordinal),
            Url: endpoint,
            Headers: new Dictionary<string, string>(StringComparer.Ordinal),
            OAuth: oauth);
    }

    private static IEnumerable<ToolCall> ToolCalls(string scenario, ExtensionRegistration registration)
    {
        if (scenario.StartsWith("auth/", StringComparison.Ordinal))
            return [new ToolCall("test-tool", new Dictionary<string, object?>(StringComparer.Ordinal))];

        return scenario switch
        {
            "initialize" => [],
            "tools_call" => [new ToolCall("add_numbers", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["a"] = 2,
                ["b"] = 3
            })],
            "sse-retry" => [new ToolCall("test_reconnection", new Dictionary<string, object?>(StringComparer.Ordinal))],
            "elicitation-sep1034-client-defaults" =>
                [new ToolCall("test_client_elicitation_defaults", new Dictionary<string, object?>(StringComparer.Ordinal))],
            "json-schema-2020-12-preservation" =>
                [new ToolCall("json_schema_echo", SchemaArguments(registration))],
            _ => throw new InvalidOperationException("Unknown scenario " + scenario)
        };
    }

    private static Dictionary<string, object?> SchemaArguments(ExtensionRegistration registration)
    {
        var tool = FindTool(registration, "json_schema_2020_12_tool");
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = tool.Function.JsonSchema.Clone()
        };
    }

    private static PiSharpToolRegistration FindTool(ExtensionRegistration registration, string toolName)
    {
        var suffix = "mcp__conformance__" + NormalizeToolName(toolName);
        return registration.ToolDefinitions.FirstOrDefault(tool => tool.Function.Name == suffix) ??
            throw new InvalidOperationException("MCP server did not advertise tool " + toolName);
    }

    private static bool HasTool(ExtensionRegistration registration, string toolName)
    {
        var expected = "mcp__conformance__" + NormalizeToolName(toolName);
        return registration.ToolDefinitions.Any(tool => tool.Function.Name == expected);
    }

    private static string NormalizeToolName(string name) => new(name.Select(character =>
        char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_').ToArray());

    private static bool IsAuthorizationRequired(string error) =>
        error.Contains("needs authorization", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsSignInRequired(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is McpSignInRequiredException) return true;
        return false;
    }

    private static async Task VisitAuthorizationUrlAsync(Uri authorizationUrl)
    {
        using var timeout = new CancellationTokenSource(s_requestTimeout);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = s_requestTimeout };
        using var authorization = await client.GetAsync(authorizationUrl, timeout.Token);
        var location = authorization.Headers.Location ??
            throw new InvalidOperationException($"Authorization endpoint answered {(int)authorization.StatusCode} without a redirect");
        var callback = location.IsAbsoluteUri ? location : new Uri(authorizationUrl, location);
        if (callback.Scheme != Uri.UriSchemeHttp || !callback.IsLoopback)
            throw new InvalidOperationException("Authorization endpoint did not redirect to a loopback callback");
        if (callback.Query.Contains("error=", StringComparison.Ordinal))
            throw new InvalidOperationException("MCP authorization was denied");
        using var response = await client.GetAsync(callback, timeout.Token);
        _ = await response.Content.ReadAsStringAsync(timeout.Token);
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadContext()
    {
        var raw = Environment.GetEnvironmentVariable("MCP_CONFORMANCE_CONTEXT");
        if (string.IsNullOrWhiteSpace(raw)) return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("MCP_CONFORMANCE_CONTEXT is not an object");
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static void WriteReport(string? path, bool success, string? error)
    {
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { success, error }, new JsonSerializerOptions
        {
            WriteIndented = true
        }) + Environment.NewLine);
    }
}
