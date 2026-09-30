using System.Text.Json;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli;

/// <summary>Manage MCP config without requiring a model, credentials, or a chat session.</summary>
public static class McpCommand
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string[] arguments, string agentDirectory, string cwd,
        TextWriter output, TextWriter errors, CancellationToken cancellationToken = default)
    {
        var command = arguments.FirstOrDefault();
        if (command is null or "help" or "--help" or "-h")
        {
            await output.WriteLineAsync("Usage: pisharp mcp list [--json] | add <server> [--local] [options] (--url <url> | -- <command> [args...]) | remove <server> [--local] | login <server> [--timeout <seconds>] | logout <server>");
            await output.WriteLineAsync("Add options: --env KEY=VALUE, --cwd DIR, --header KEY=VALUE, --bearer-token-env-var NAME, --exposure MODE, --oauth-client-id ID, --oauth-client-secret VALUE, --oauth-callback-url URL, --oauth-scope SCOPE, --oauth-client-name NAME.");
            return 0;
        }
        try
        {
            return command switch
            {
                "list" => await ListAsync(arguments[1..], agentDirectory, cwd, output, errors, cancellationToken),
                "add" => await AddAsync(arguments[1..], agentDirectory, cwd, output, errors, cancellationToken),
                "remove" => await RemoveAsync(arguments[1..], agentDirectory, cwd, output, errors, cancellationToken),
                "login" => await LoginAsync(arguments[1..], agentDirectory, cwd, output, errors, cancellationToken),
                "logout" => await LogoutAsync(arguments[1..], agentDirectory, cwd, output, errors, cancellationToken),
                _ => await FailAsync(errors, "Unknown MCP command: " + command)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is ArgumentException or IOException or JsonException or
            InvalidDataException or UnauthorizedAccessException or PlatformNotSupportedException or
            InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            // A config or transport error may include a credential value. Report its category only.
            return await FailAsync(errors, "MCP command failed (" + exception.GetType().Name + ").");
        }
    }

    private static async Task<int> ListAsync(string[] arguments, string agentDirectory, string cwd,
        TextWriter output, TextWriter errors, CancellationToken cancellationToken)
    {
        if (arguments.Length > 1 || arguments.Length == 1 && arguments[0] != "--json")
            return await FailAsync(errors, "Usage: pisharp mcp list [--json]");
        var trusted = await new ProjectTrust(agentDirectory).GetAsync(cwd, cancellationToken) == true;
        var configuration = await McpConfiguration.LoadAsync(agentDirectory, cwd, trusted, cancellationToken);
        using var catalog = ExtensionCatalog.Load(agentDirectory, cwd, trusted, discover: false);
        var failures = await McpRuntime.RegisterForStatusAsync(configuration, catalog, cwd, cancellationToken);
        var reports = configuration.Servers.Select(server =>
        {
            var problem = failures.FirstOrDefault(error => error.StartsWith("MCP server " + server.Name + " ",
                StringComparison.Ordinal));
            var tools = catalog.Registration.ToolDefinitions.Where(tool =>
                tool.Function.Name.StartsWith("mcp__" + server.Name + "__", StringComparison.Ordinal))
                .Select(tool => tool.Function.Name).ToArray();
            return new
            {
                name = server.Name,
                scope = server.Scope,
                source = server.SourcePath,
                enabled = server.Enabled,
                exposure = server.Exposure.ToString().ToLowerInvariant().Replace("codemodedeferred", "codemode-deferred", StringComparison.Ordinal),
                transport = server.Command is null ? "http" : "stdio",
                state = !server.Enabled ? "disabled" : problem?.Contains("needs authorization",
                    StringComparison.Ordinal) == true ? "needs-auth" :
                    problem?.Contains("still connecting", StringComparison.Ordinal) == true ? "connecting" :
                    problem is not null ? "failed" : "connected",
                tools,
                error = problem
            };
        }).ToArray();
        var untrusted = !trusted && File.Exists(Path.Combine(cwd, ".pi", "mcp.json"));
        if (arguments.Contains("--json", StringComparer.Ordinal))
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                servers = reports,
                errors = failures.Where(error => !reports.Any(report => report.error == error)).ToArray(),
                projectIgnored = untrusted
            }, s_json));
        else
        {
            foreach (var report in reports)
            {
                await output.WriteLineAsync(report.name + ": " + report.state + " (" + report.exposure + ", " +
                    report.scope + ")");
                if (report.tools.Length > 0) await output.WriteLineAsync("  tools: " + string.Join(", ", report.tools));
                if (report.error is not null) await output.WriteLineAsync("  " + report.error);
            }
            foreach (var failure in failures.Where(error => !reports.Any(report => report.error == error)))
                await output.WriteLineAsync("config error: " + failure);
            if (untrusted) await output.WriteLineAsync("Project .pi/mcp.json is ignored until the project is trusted.");
        }
        return failures.Count == 0 ? 0 : 1;
    }

    private static async Task<int> AddAsync(string[] arguments, string agentDirectory, string cwd,
        TextWriter output, TextWriter errors, CancellationToken cancellationToken)
    {
        string? name = null, url = null, workdir = null, exposure = null, bearer = null;
        string? oauthClientId = null, oauthClientSecret = null, oauthCallbackUrl = null, oauthScope = null,
            oauthClientName = null;
        var local = false;
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var command = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            var arg = arguments[index];
            if (arg == "--") { command.AddRange(arguments[(index + 1)..]); break; }
            if (name is not null && !arg.StartsWith('-')) { command.AddRange(arguments[index..]); break; }
            if (arg is "-l" or "--local") { local = true; continue; }
            if (arg is "--url" or "--cwd" or "--exposure" or "--env" or "--header" or
                "--bearer-token-env-var" or "--oauth-client-id" or "--oauth-client-secret" or
                "--oauth-callback-url" or "--oauth-scope" or "--oauth-client-name")
            {
                if (++index >= arguments.Length) return await FailAsync(errors, arg + " needs a value.");
                var value = arguments[index];
                switch (arg)
                {
                    case "--url": url = value; break;
                    case "--cwd": workdir = value; break;
                    case "--exposure": exposure = value; break;
                    case "--bearer-token-env-var": bearer = value; break;
                    case "--oauth-client-id": oauthClientId = value; break;
                    case "--oauth-client-secret": oauthClientSecret = value; break;
                    case "--oauth-callback-url": oauthCallbackUrl = value; break;
                    case "--oauth-scope": oauthScope = value; break;
                    case "--oauth-client-name": oauthClientName = value; break;
                    case "--env": if (!AddPair(env, value)) return await FailAsync(errors, "--env expects KEY=VALUE."); break;
                    case "--header": if (!AddPair(headers, value)) return await FailAsync(errors, "--header expects KEY=VALUE."); break;
                }
                continue;
            }
            if (arg.StartsWith('-')) return await FailAsync(errors, "Unknown MCP option: " + arg);
            if (name is null) { name = arg; continue; }
            command.AddRange(arguments[index..]);
            break;
        }
        if (name is null || url is null == (command.Count == 0))
            return await FailAsync(errors, "Usage: pisharp mcp add <server> [options] (--url <url> | -- <command> [args...])");
        if (url is not null && (env.Count > 0 || workdir is not null) ||
            command.Count > 0 && (headers.Count > 0 || bearer is not null || oauthClientId is not null ||
                oauthClientSecret is not null || oauthCallbackUrl is not null || oauthScope is not null ||
                oauthClientName is not null))
            return await FailAsync(errors, "MCP transport options do not match the server type.");
        if (oauthClientName is not null && string.IsNullOrWhiteSpace(oauthClientName))
            return await FailAsync(errors, "OAuth client name must be a non-empty string.");
        if ((bearer is not null || headers.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))) &&
            (oauthClientId is not null || oauthClientSecret is not null || oauthCallbackUrl is not null ||
                oauthScope is not null || oauthClientName is not null))
            return await FailAsync(errors, "MCP OAuth cannot be combined with an Authorization header.");
        if (bearer is not null)
        {
            if (bearer.Length == 0 || !(char.IsAsciiLetter(bearer[0]) || bearer[0] == '_') ||
                !bearer.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
                return await FailAsync(errors, "Bearer token environment name is invalid.");
            headers["Authorization"] = "Bearer ${" + bearer + "}";
        }
        object? oauth = oauthClientId is null && oauthClientSecret is null && oauthCallbackUrl is null &&
            oauthScope is null && oauthClientName is null ? null : new
            {
                clientId = oauthClientId,
                clientSecret = oauthClientSecret,
                callbackUrl = oauthCallbackUrl,
                scope = oauthScope,
                clientName = oauthClientName
            };
        object server = url is not null
            ? new { url, headers = headers.Count == 0 ? null : headers, exposure, oauth }
            : new
            {
                command = command[0],
                args = command.Skip(1).ToArray(),
                env = env.Count == 0 ? null : env,
                cwd = workdir,
                exposure
            };
        var node = JsonSerializer.SerializeToElement(server, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        var path = ConfigPath(agentDirectory, cwd, local);
        var replaced = await McpConfigurationEditor.AddAsync(path, name, node, cancellationToken);
        await output.WriteLineAsync((replaced ? "Replaced " : "Added ") + (local ? "project" : "global") +
            " MCP server \"" + name + "\" in " + path + ".");
        if (local && await new ProjectTrust(agentDirectory).GetAsync(cwd, cancellationToken) != true)
            await output.WriteLineAsync("The project is not trusted, so its MCP config is ignored until trust is granted.");
        return 0;
    }

    private static async Task<int> RemoveAsync(string[] arguments, string agentDirectory, string cwd,
        TextWriter output, TextWriter errors, CancellationToken cancellationToken)
    {
        var local = arguments.Contains("--local", StringComparer.Ordinal) || arguments.Contains("-l", StringComparer.Ordinal);
        var names = arguments.Where(argument => argument is not ("--local" or "-l")).ToArray();
        if (names.Length != 1 || names[0].StartsWith('-'))
            return await FailAsync(errors, "Usage: pisharp mcp remove <server> [--local]");
        var path = ConfigPath(agentDirectory, cwd, local);
        if (!await McpConfigurationEditor.RemoveAsync(path, names[0], cancellationToken))
            return await FailAsync(errors, "No " + (local ? "project" : "global") + " MCP server named \"" + names[0] + "\".");
        await output.WriteLineAsync("Removed " + (local ? "project" : "global") + " MCP server \"" + names[0] + "\".");
        return 0;
    }

    private static string ConfigPath(string agentDirectory, string cwd, bool local) =>
        local ? Path.Combine(cwd, ".pi", "mcp.json") : Path.Combine(agentDirectory, "mcp.json");

    private static async Task<int> LoginAsync(string[] arguments, string agentDirectory, string cwd,
        TextWriter output, TextWriter errors, CancellationToken cancellationToken)
    {
        if (arguments.Length is not (1 or 3) || arguments[0].StartsWith('-') ||
            arguments.Length == 3 && (arguments[1] != "--timeout" ||
                !int.TryParse(arguments[2], out var seconds) || seconds is <= 0 or > 3600))
            return await FailAsync(errors, "Usage: pisharp mcp login <server> [--timeout <seconds>]");
        var timeout = arguments.Length == 3 ? int.Parse(arguments[2], System.Globalization.CultureInfo.InvariantCulture) : 300;
        var server = await FindServerAsync(arguments[0], agentDirectory, cwd, errors, cancellationToken);
        if (server is null) return 1;
        if (server.Url is null || server.Headers.Keys.Any(key =>
            key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
            return await FailAsync(errors, "MCP server \"" + server.Name + "\" does not use OAuth.");
        try
        {
            await McpOAuthLogin.SignInAsync(server, agentDirectory, output, ReferenceEquals(output, Console.Out),
                TimeSpan.FromSeconds(timeout), cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(errors, "MCP sign-in timed out.");
        }
    }

    private static async Task<int> LogoutAsync(string[] arguments, string agentDirectory, string cwd,
        TextWriter output, TextWriter errors, CancellationToken cancellationToken)
    {
        if (arguments.Length != 1 || arguments[0].StartsWith('-'))
            return await FailAsync(errors, "Usage: pisharp mcp logout <server>");
        var server = await FindServerAsync(arguments[0], agentDirectory, cwd, errors, cancellationToken);
        if (server is null) return 1;
        if (server.Url is null || server.Headers.Keys.Any(key =>
            key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
            return await FailAsync(errors, "MCP server \"" + server.Name + "\" does not use OAuth.");
        var removed = await new McpTokenCache(agentDirectory).RemoveAsync(server.Url, cancellationToken);
        await output.WriteLineAsync(removed ? "Signed out of MCP server \"" + server.Name + "\"." :
            "No saved sign-in for MCP server \"" + server.Name + "\".");
        return 0;
    }

    private static async Task<McpServerConfiguration?> FindServerAsync(string name, string agentDirectory,
        string cwd, TextWriter errors, CancellationToken cancellationToken)
    {
        var trusted = await new ProjectTrust(agentDirectory).GetAsync(cwd, cancellationToken) == true;
        var configuration = await McpConfiguration.LoadAsync(agentDirectory, cwd, trusted, cancellationToken);
        var server = configuration.Servers.FirstOrDefault(item => item.Name == name);
        if (server is null)
            await errors.WriteLineAsync("No configured MCP server named \"" + name + "\".");
        return server;
    }

    private static bool AddPair(IDictionary<string, string> values, string pair)
    {
        var separator = pair.IndexOf('=');
        if (separator <= 0) return false;
        values[pair[..separator]] = pair[(separator + 1)..];
        return true;
    }

    private static async Task<int> FailAsync(TextWriter errors, string message)
    {
        await errors.WriteLineAsync(message);
        return 1;
    }
}
